#define WIN32_LEAN_AND_MEAN
#include <winsock2.h>
#include <ws2bth.h>
#include <bluetoothapis.h>
#include <windows.h>
#include <stdio.h>
#include <stdlib.h>
#include <stdint.h>
#include <stdbool.h>
#include <string.h>
#include <wchar.h>

#pragma comment(lib, "ws2_32.lib")
#pragma comment(lib, "bthprops.lib")

#define ARRAY_LEN(x) (sizeof(x) / sizeof((x)[0]))
#define MDR_START 0x3e
#define MDR_END 0x3c
#define MDR_ESC 0x3d
#define MAX_DEVICES 64
#define MAX_PAYLOAD 4096
#define MAX_FRAME 8192

static const GUID SONY_TABLE2_UUID = { 0x956c7b26, 0xd49a, 0x4ba8, { 0xb0, 0x3f, 0xb1, 0x7d, 0x39, 0x3c, 0xb6, 0xe2 } };
static const GUID SONY_TABLE1_UUID = { 0x96cc203e, 0x5068, 0x46ad, { 0xb3, 0x2d, 0xe3, 0x16, 0xf5, 0xe0, 0x69, 0xba } };

typedef struct {
    BLUETOOTH_ADDRESS addr;
    wchar_t name[BLUETOOTH_MAX_NAME_SIZE];
    BOOL connected;
    BOOL remembered;
    BOOL authenticated;
} bt_device_t;

typedef struct {
    bool valid;
    char error[160];
    uint8_t data_type;
    uint8_t sequence;
    uint8_t payload[MAX_PAYLOAD];
    size_t payload_len;
    bool ack_required;
} mdr_frame_t;

typedef struct {
    bool in_frame;
    uint8_t buf[MAX_FRAME];
    size_t len;
} parser_t;

typedef struct {
    const char *action;
    const char *name_filter;
    int timeout_ms;
    const char *batch_text;
    uint8_t raw_payload[MAX_PAYLOAD];
    size_t raw_len;
    uint8_t data_type;
    bool no_ack;
    bool ack_only;
    int ambient_level;
    /* NC/ambient changes; -1 leaves the current device value alone. */
    int set_mode;        /* 0 = off, 1 = anc, 2 = ambient */
    int set_level;       /* 0..20 */
    int set_voice;       /* 0 = normal, 1 = voice */
    int set_auto;        /* 0 = off, 1 = on */
    int set_sensitivity; /* 0 = standard, 1 = high, 2 = low */
} options_t;

static void print_usage(void) {
    puts("xm5ctl - Sony WH-1000XM5 native Windows RFCOMM control");
    puts("");
    puts("Usage:");
    puts("  xm5ctl scan [--name TEXT]");
    puts("  xm5ctl battery [--timeout MS] [--name TEXT]");
    puts("  xm5ctl ncasm [--timeout MS] [--name TEXT]");
    puts("  xm5ctl ncasm-set [--mode anc|ambient|off] [--level 0..20] [--voice on|off]");
    puts("                   [--auto on|off] [--sensitivity low|standard|high]");
    puts("  xm5ctl listen [--timeout MS] [--hex]");
    puts("  xm5ctl raw \"22 00\" [--data-type mdr|mdr2] [--ack-only] [--no-ack]");
    puts("  xm5ctl batch \"22 00;66 17;E6 01\" [--timeout MS] [--name TEXT]");
    puts("  xm5ctl anc");
    puts("  xm5ctl ambient 12");
    puts("  xm5ctl off");
}

static void die_wsa(const char *what) {
    fprintf(stderr, "%s failed: WSA error %d\n", what, WSAGetLastError());
}

static void print_addr(BLUETOOTH_ADDRESS a) {
    printf("%02X:%02X:%02X:%02X:%02X:%02X",
        a.rgBytes[5], a.rgBytes[4], a.rgBytes[3],
        a.rgBytes[2], a.rgBytes[1], a.rgBytes[0]);
}

static bool wide_contains_ascii(const wchar_t *haystack, const char *needle) {
    wchar_t wneedle[BLUETOOTH_MAX_NAME_SIZE];
    size_t i = 0;

    if (!needle || !*needle) {
        return true;
    }
    for (; needle[i] && i + 1 < ARRAY_LEN(wneedle); i++) {
        char c = needle[i];
        if (c >= 'A' && c <= 'Z') c = (char)(c - 'A' + 'a');
        wneedle[i] = (wchar_t)(unsigned char)c;
    }
    wneedle[i] = L'\0';

    for (const wchar_t *p = haystack; *p; p++) {
        size_t j = 0;
        while (wneedle[j] && p[j]) {
            wchar_t hc = p[j];
            if (hc >= L'A' && hc <= L'Z') hc = (wchar_t)(hc - L'A' + L'a');
            if (hc != wneedle[j]) break;
            j++;
        }
        if (!wneedle[j]) {
            return true;
        }
    }
    return false;
}

static int enumerate_devices(bt_device_t *devices, int max_devices) {
    BLUETOOTH_DEVICE_SEARCH_PARAMS params;
    BLUETOOTH_DEVICE_INFO info;
    HBLUETOOTH_DEVICE_FIND find;
    int count = 0;

    ZeroMemory(&params, sizeof(params));
    params.dwSize = sizeof(params);
    params.fReturnAuthenticated = TRUE;
    params.fReturnRemembered = TRUE;
    params.fReturnConnected = TRUE;
    params.fReturnUnknown = FALSE;
    params.fIssueInquiry = FALSE;
    params.cTimeoutMultiplier = 1;
    params.hRadio = NULL;

    ZeroMemory(&info, sizeof(info));
    info.dwSize = sizeof(info);

    find = BluetoothFindFirstDevice(&params, &info);
    if (!find) {
        return 0;
    }

    do {
        if (count < max_devices) {
            devices[count].addr = info.Address;
            wcsncpy(devices[count].name, info.szName, ARRAY_LEN(devices[count].name) - 1);
            devices[count].name[ARRAY_LEN(devices[count].name) - 1] = L'\0';
            devices[count].connected = info.fConnected;
            devices[count].remembered = info.fRemembered;
            devices[count].authenticated = info.fAuthenticated;
            count++;
        }
        ZeroMemory(&info, sizeof(info));
        info.dwSize = sizeof(info);
    } while (BluetoothFindNextDevice(find, &info));

    BluetoothFindDeviceClose(find);
    return count;
}

static void list_devices(const char *name_filter) {
    bt_device_t devices[MAX_DEVICES];
    int count = enumerate_devices(devices, MAX_DEVICES);

    printf("%-6s %-6s %-6s %-17s %ls\n", "Conn", "Auth", "Rem", "Address", L"Name");
    for (int i = 0; i < count; i++) {
        if (!wide_contains_ascii(devices[i].name, name_filter)) {
            continue;
        }
        printf("%-6s %-6s %-6s ",
            devices[i].connected ? "yes" : "no",
            devices[i].authenticated ? "yes" : "no",
            devices[i].remembered ? "yes" : "no");
        print_addr(devices[i].addr);
        wprintf(L" %ls\n", devices[i].name);
    }
}

static uint8_t checksum(const uint8_t *bytes, size_t len) {
    uint32_t sum = 0;
    for (size_t i = 0; i < len; i++) {
        sum = (sum + bytes[i]) & 0xffu;
    }
    return (uint8_t)sum;
}

static bool data_type_ack_required(uint8_t data_type) {
    switch (data_type) {
    case 0x01:
    case 0x1c:
    case 0x1d:
    case 0x1e:
        return false;
    default:
        return true;
    }
}

static const char *data_type_name(uint8_t data_type) {
    switch (data_type) {
    case 0x00: return "DATA";
    case 0x01: return "ACK";
    case 0x0c: return "DATA_MDR";
    case 0x0d: return "DATA_COMMON";
    case 0x0e: return "DATA_MDR_NO2";
    case 0x1c: return "SHOT_MDR";
    case 0x1e: return "SHOT_MDR_NO2";
    default: return "UNKNOWN";
    }
}

static void print_hex(const uint8_t *bytes, size_t len) {
    for (size_t i = 0; i < len; i++) {
        printf("%s%02X", i ? " " : "", bytes[i]);
    }
}

static bool build_frame(uint8_t data_type, uint8_t seq, const uint8_t *payload, size_t payload_len,
                        uint8_t *out, size_t out_cap, size_t *out_len) {
    uint8_t inner[MAX_FRAME];
    size_t n = 0;
    size_t o = 0;

    if (payload_len > MAX_PAYLOAD || payload_len + 8 > sizeof(inner)) {
        return false;
    }

    inner[n++] = data_type;
    inner[n++] = seq;
    inner[n++] = (uint8_t)((payload_len >> 24) & 0xff);
    inner[n++] = (uint8_t)((payload_len >> 16) & 0xff);
    inner[n++] = (uint8_t)((payload_len >> 8) & 0xff);
    inner[n++] = (uint8_t)(payload_len & 0xff);
    memcpy(inner + n, payload, payload_len);
    n += payload_len;
    inner[n++] = checksum(inner, n);

    if (o >= out_cap) return false;
    out[o++] = MDR_START;
    for (size_t i = 0; i < n; i++) {
        uint8_t b = inner[i];
        if (b == MDR_START || b == MDR_END || b == MDR_ESC) {
            if (o + 2 > out_cap) return false;
            out[o++] = MDR_ESC;
            out[o++] = (uint8_t)(b ^ 0x10);
        } else {
            if (o + 1 > out_cap) return false;
            out[o++] = b;
        }
    }
    if (o >= out_cap) return false;
    out[o++] = MDR_END;
    *out_len = o;
    return true;
}

static void parse_inner(const uint8_t *escaped, size_t escaped_len, mdr_frame_t *frame) {
    uint8_t raw[MAX_FRAME];
    size_t r = 0;

    ZeroMemory(frame, sizeof(*frame));
    for (size_t i = 0; i < escaped_len; i++) {
        uint8_t b = escaped[i];
        if (b == MDR_ESC) {
            if (++i >= escaped_len) {
                snprintf(frame->error, sizeof(frame->error), "escape byte at end of frame");
                return;
            }
            b = (uint8_t)(escaped[i] ^ 0x10);
        }
        if (r >= sizeof(raw)) {
            snprintf(frame->error, sizeof(frame->error), "frame too large");
            return;
        }
        raw[r++] = b;
    }

    if (r < 7) {
        snprintf(frame->error, sizeof(frame->error), "frame too short");
        return;
    }
    if (checksum(raw, r - 1) != raw[r - 1]) {
        snprintf(frame->error, sizeof(frame->error), "checksum mismatch");
        return;
    }

    size_t payload_len = ((size_t)raw[2] << 24) | ((size_t)raw[3] << 16) | ((size_t)raw[4] << 8) | raw[5];
    if (payload_len != r - 7 || payload_len > MAX_PAYLOAD) {
        snprintf(frame->error, sizeof(frame->error), "payload length mismatch");
        return;
    }

    frame->valid = true;
    frame->data_type = raw[0];
    frame->sequence = raw[1];
    frame->payload_len = payload_len;
    frame->ack_required = data_type_ack_required(raw[0]);
    if (payload_len) {
        memcpy(frame->payload, raw + 6, payload_len);
    }
}

static int parser_add(parser_t *parser, const uint8_t *bytes, size_t len, mdr_frame_t *frames, int max_frames) {
    int count = 0;
    for (size_t i = 0; i < len; i++) {
        uint8_t b = bytes[i];
        if (!parser->in_frame) {
            if (b == MDR_START) {
                parser->in_frame = true;
                parser->len = 0;
            }
            continue;
        }
        if (b == MDR_START) {
            parser->len = 0;
            parser->in_frame = true;
            continue;
        }
        if (b == MDR_END) {
            if (count < max_frames) {
                parse_inner(parser->buf, parser->len, &frames[count++]);
            }
            parser->len = 0;
            parser->in_frame = false;
            continue;
        }
        if (parser->len < sizeof(parser->buf)) {
            parser->buf[parser->len++] = b;
        } else {
            parser->len = 0;
            parser->in_frame = false;
        }
    }
    return count;
}

static bool g_hex_dump = false;

static const char *charging_text(uint8_t b) {
    switch (b) {
    case 0: return "not-charging";
    case 1: return "charging";
    case 2: return "unknown";
    case 3: return "charged";
    case 240: return "unknown";
    default: return "unknown";
    }
}

static const char *ncasm_type_text(uint8_t b) {
    switch (b) {
    case 0x00: return "none";
    case 0x01: return "nc";
    case 0x02: return "combined";
    case 0x03: return "ambient";
    case 0x11: return "nc-ambient-onoff";
    case 0x12: return "mode-onoff";
    case 0x13: return "seamless";
    case 0x14: return "mode-seamless";
    case 0x15: return "auto-mode-seamless";
    case 0x16: return "dual-single-mode-seamless";
    case 0x17: return "dual-mode-seamless";
    case 0x18: return "ncss-dual-mode-seamless";
    case 0x19: return "dual-mode-seamless-na";
    case 0x22: return "ambient-seamless";
    default: return "unknown";
    }
}

static const char *onoff_text(uint8_t b) {
    switch (b) {
    case 0: return "off";
    case 1: return "on";
    default: return "unknown";
    }
}

static const char *ambient_mode_text(uint8_t b) {
    switch (b) {
    case 0: return "normal";
    case 1: return "voice";
    default: return "unknown";
    }
}

/* Auto ambient sensitivity, reported by NCASM type 0x19 (WF-1000XM6). */
static const char *sensitivity_text(uint8_t b) {
    switch (b) {
    case 0: return "standard";
    case 1: return "high";
    case 2: return "low";
    default: return "unknown";
    }
}

static bool sensitivity_value(const char *text, uint8_t *out) {
    if (strcmp(text, "standard") == 0) { *out = 0; return true; }
    if (strcmp(text, "high") == 0) { *out = 1; return true; }
    if (strcmp(text, "low") == 0) { *out = 2; return true; }
    return false;
}

/*
 * NC/ambient state. Older models answer NCASM inquired type 0x17 with five
 * parameter bytes; the WF-1000XM6 answers type 0x19 with seven, adding the
 * auto ambient toggle and its sensitivity.
 */
typedef struct {
    bool valid;
    uint8_t type;
    uint8_t effect;
    uint8_t master;
    uint8_t mode;
    uint8_t voice;
    uint8_t level;
    bool has_auto;
    uint8_t asm_auto;
    uint8_t sensitivity;
} ncasm_state_t;

/*
 * Decode an NCASM state frame. Pass want_type 0x17 or 0x19 to accept only a
 * reply to that inquired type, or 0 to accept either. A frame carrying a type
 * other than the one that was asked for says nothing about the request still
 * outstanding, and treating it as the answer lets a stray notification stand in
 * for the real state.
 */
static bool parse_ncasm(const uint8_t *p, size_t n, uint8_t want_type, ncasm_state_t *out) {
    if (n < 7 || (p[0] != 0x67 && p[0] != 0x69)) {
        return false;
    }
    if (p[1] != 0x17 && p[1] != 0x19) {
        return false;
    }
    if (want_type != 0 && p[1] != want_type) {
        return false;
    }
    ZeroMemory(out, sizeof(*out));
    out->type = p[1];
    out->effect = p[2];
    out->master = p[3];
    out->mode = p[4];
    out->voice = p[5];
    out->level = p[6];
    if (p[1] == 0x19) {
        if (n < 9) return false;
        out->has_auto = true;
        out->asm_auto = p[7];
        out->sensitivity = p[8];
    }
    out->valid = true;
    return true;
}

static void print_ncasm_state(const ncasm_state_t *st) {
    printf("ncasm: type=%02X; master %s; mode %s; voice %s; level %u",
        st->type,
        onoff_text(st->master),
        st->mode == 0 ? "anc" : (st->mode == 1 ? "ambient" : "unknown"),
        onoff_text(st->voice),
        st->level);
    if (st->has_auto) {
        printf("; auto %s; sensitivity %s", onoff_text(st->asm_auto), sensitivity_text(st->sensitivity));
    }
    putchar('\n');
}

static void print_known_payload(const mdr_frame_t *frame) {
    const uint8_t *p = frame->payload;
    size_t n = frame->payload_len;

    if (g_hex_dump) {
        printf("RX %s seq=%u %s raw: ", data_type_name(frame->data_type), frame->sequence, frame->valid ? "ok" : "bad");
        print_hex(p, n);
        putchar('\n');
    }

    if (!frame->valid) {
        printf("invalid frame: %s\n", frame->error);
        return;
    }
    if (n == 0) {
        printf("%s seq=%u empty\n", data_type_name(frame->data_type), frame->sequence);
        return;
    }

    switch (p[0]) {
    case 0x11:
    case 0x13:
    case 0x23:
    case 0x25:
        if (n >= 4 && p[1] == 0x00) {
            printf("battery: %u%% (%s)\n", p[2], charging_text(p[3]));
        } else if (n >= 6 && p[1] == 0x01) {
            printf("battery: left %u%% (%s), right %u%% (%s)\n", p[2], charging_text(p[3]), p[4], charging_text(p[5]));
        } else if (n >= 4 && p[1] == 0x02) {
            /*
             * No charging state here. The WF-1000XM6 reports a constant 0x01 in
             * p[3] whether or not the case is on power, and the level does not
             * move while it charges, so both look like values cached from the
             * last time the buds were docked. Report only the level.
             */
            printf("case battery: %u%%\n", p[2]);
        } else {
            printf("battery: ");
            print_hex(p, n);
            putchar('\n');
        }
        return;
    case 0x63:
    case 0x65:
        if (n >= 3) {
            printf("ncasm status: %s %s\n", ncasm_type_text(p[1]), p[2] == 0 ? "enable" : (p[2] == 1 ? "disable" : "unknown"));
        } else {
            puts("ncasm status: malformed response");
        }
        return;
    case 0x67:
    case 0x69: {
        ncasm_state_t st;
        if (parse_ncasm(p, n, 0, &st)) {
            print_ncasm_state(&st);
        } else if (n >= 7 && p[1] == 0x13) {
            printf("ncasm: %s; master %s; anc %s; ambient %s=%u\n",
                p[2] == 1 ? "changed" : "changing", onoff_text(p[3]), onoff_text(p[4]), ambient_mode_text(p[5]), p[6]);
        } else {
            printf("ncasm param: ");
            print_hex(p, n);
            putchar('\n');
        }
        return;
    }
    default:
        printf("%s seq=%u payload: ", data_type_name(frame->data_type), frame->sequence);
        print_hex(p, n);
        putchar('\n');
        return;
    }
}

static bool send_all(SOCKET s, const uint8_t *bytes, size_t len) {
    size_t sent = 0;
    while (sent < len) {
        int n = send(s, (const char *)bytes + sent, (int)(len - sent), 0);
        if (n <= 0) return false;
        sent += (size_t)n;
    }
    return true;
}

static bool send_ack(SOCKET s, uint8_t received_sequence) {
    uint8_t frame[MAX_FRAME];
    size_t frame_len = 0;
    uint8_t ack_seq = (uint8_t)(1u - received_sequence);
    if (!build_frame(0x01, ack_seq, NULL, 0, frame, sizeof(frame), &frame_len)) {
        return false;
    }
    return send_all(s, frame, frame_len);
}

static int recv_frames(SOCKET s, parser_t *parser, int timeout_ms, mdr_frame_t *frames, int max_frames) {
    uint8_t buf[1024];
    fd_set readfds;
    struct timeval tv;
    int ready;
    int n;

    FD_ZERO(&readfds);
    FD_SET(s, &readfds);
    tv.tv_sec = timeout_ms / 1000;
    tv.tv_usec = (timeout_ms % 1000) * 1000;

    ready = select(0, &readfds, NULL, NULL, &tv);
    if (ready <= 0) {
        return 0;
    }
    n = recv(s, (char *)buf, sizeof(buf), 0);
    if (n <= 0) {
        return -1;
    }
    return parser_add(parser, buf, (size_t)n, frames, max_frames);
}

static bool is_expected_response(const mdr_frame_t *frame, int expected_command) {
    if (expected_command < 0 || !frame->valid || frame->payload_len == 0) {
        return false;
    }
    if (expected_command == 0x23) {
        return frame->payload[0] == 0x11 || frame->payload[0] == 0x13 ||
               frame->payload[0] == 0x23 || frame->payload[0] == 0x25;
    }
    return frame->payload[0] == (uint8_t)expected_command;
}

static int send_payload_seq(SOCKET s, const uint8_t *payload, size_t payload_len, uint8_t data_type,
                        uint8_t tx_sequence, int timeout_ms, bool no_ack, int expected_command,
                        mdr_frame_t *responses, int max_responses) {
    uint8_t tx[MAX_FRAME];
    size_t tx_len = 0;
    parser_t parser = { 0 };
    bool acked = !data_type_ack_required(data_type) || no_ack;
    int response_count = 0;
    DWORD start;

    if (!build_frame(data_type, tx_sequence, payload, payload_len, tx, sizeof(tx), &tx_len)) {
        fprintf(stderr, "Could not build frame.\n");
        return -1;
    }
    if (!send_all(s, tx, tx_len)) {
        die_wsa("send");
        return -1;
    }

    start = GetTickCount();
    while (!acked && (int)(GetTickCount() - start) < timeout_ms) {
        int remaining = timeout_ms - (int)(GetTickCount() - start);
        mdr_frame_t rx[8];
        int count = recv_frames(s, &parser, remaining > 50 ? remaining : 50, rx, (int)ARRAY_LEN(rx));
        if (count < 0) return -1;
        for (int i = 0; i < count; i++) {
            if (!rx[i].valid) {
                if (response_count < max_responses) responses[response_count++] = rx[i];
                continue;
            }
            if (rx[i].data_type == 0x01) {
                if (rx[i].sequence == (uint8_t)(1u - tx_sequence)) acked = true;
                continue;
            }
            if (rx[i].ack_required) {
                send_ack(s, rx[i].sequence);
            }
            if (response_count < max_responses) responses[response_count++] = rx[i];
            if (is_expected_response(&rx[i], expected_command)) {
                return response_count;
            }
        }
    }
    if (!acked) {
        fprintf(stderr, "Remote endpoint did not ACK the command.\n");
        return -1;
    }
    if (expected_command == -2) {
        return 0;
    }

    start = GetTickCount();
    while (response_count < max_responses && (int)(GetTickCount() - start) < timeout_ms) {
        int remaining = timeout_ms - (int)(GetTickCount() - start);
        mdr_frame_t rx[8];
        int count = recv_frames(s, &parser, remaining > 50 ? remaining : 50, rx, (int)ARRAY_LEN(rx));
        if (count < 0) return -1;
        for (int i = 0; i < count; i++) {
            if (rx[i].valid && rx[i].ack_required) {
                send_ack(s, rx[i].sequence);
            }
            if (rx[i].valid && rx[i].data_type == 0x01) {
                continue;
            }
            if (response_count < max_responses) responses[response_count++] = rx[i];
            if (is_expected_response(&rx[i], expected_command)) {
                return response_count;
            }
        }
    }
    return response_count;
}

static int send_payload(SOCKET s, const uint8_t *payload, size_t payload_len, uint8_t data_type,
                        int timeout_ms, bool no_ack, int expected_command,
                        mdr_frame_t *responses, int max_responses) {
    return send_payload_seq(s, payload, payload_len, data_type, 0, timeout_ms, no_ack, expected_command, responses, max_responses);
}

static SOCKET connect_addr(BLUETOOTH_ADDRESS addr, const GUID *service_uuid, int timeout_ms) {
    SOCKET s = socket(AF_BTH, SOCK_STREAM, BTHPROTO_RFCOMM);
    SOCKADDR_BTH sa;
    DWORD ms = (DWORD)timeout_ms;
    u_long nonblocking = 1;
    u_long blocking = 0;
    int rc;

    if (s == INVALID_SOCKET) {
        die_wsa("socket");
        return INVALID_SOCKET;
    }
    setsockopt(s, SOL_SOCKET, SO_RCVTIMEO, (const char *)&ms, sizeof(ms));
    setsockopt(s, SOL_SOCKET, SO_SNDTIMEO, (const char *)&ms, sizeof(ms));

    ZeroMemory(&sa, sizeof(sa));
    sa.addressFamily = AF_BTH;
    sa.btAddr = addr.ullLong;
    sa.serviceClassId = *service_uuid;
    sa.port = BT_PORT_ANY;

    if (ioctlsocket(s, FIONBIO, &nonblocking) == SOCKET_ERROR) {
        closesocket(s);
        return INVALID_SOCKET;
    }

    rc = connect(s, (const struct sockaddr *)&sa, sizeof(sa));
    if (rc == SOCKET_ERROR) {
        int err = WSAGetLastError();
        if (err == WSAEWOULDBLOCK || err == WSAEINPROGRESS || err == WSAEINVAL) {
            fd_set writefds;
            fd_set exceptfds;
            struct timeval tv;

            FD_ZERO(&writefds);
            FD_ZERO(&exceptfds);
            FD_SET(s, &writefds);
            FD_SET(s, &exceptfds);
            tv.tv_sec = timeout_ms / 1000;
            tv.tv_usec = (timeout_ms % 1000) * 1000;

            rc = select(0, NULL, &writefds, &exceptfds, &tv);
            if (rc > 0 && FD_ISSET(s, &writefds) && !FD_ISSET(s, &exceptfds)) {
                int so_error = 0;
                int so_error_len = sizeof(so_error);
                if (getsockopt(s, SOL_SOCKET, SO_ERROR, (char *)&so_error, &so_error_len) == SOCKET_ERROR || so_error != 0) {
                    closesocket(s);
                    return INVALID_SOCKET;
                }
            } else {
                closesocket(s);
                return INVALID_SOCKET;
            }
        } else {
            closesocket(s);
            return INVALID_SOCKET;
        }
    }

    if (ioctlsocket(s, FIONBIO, &blocking) == SOCKET_ERROR) {
        closesocket(s);
        return INVALID_SOCKET;
    }
    return s;
}

static SOCKET connect_best(const char *name_filter, int timeout_ms, wchar_t *selected_name, size_t selected_name_len) {
    bt_device_t devices[MAX_DEVICES];
    int count = enumerate_devices(devices, MAX_DEVICES);

    for (int pass = 0; pass < 2; pass++) {
        for (int i = 0; i < count; i++) {
            SOCKET s;
            if (!wide_contains_ascii(devices[i].name, name_filter)) continue;
            if (pass == 0 && !devices[i].connected) continue;

            s = connect_addr(devices[i].addr, &SONY_TABLE2_UUID, timeout_ms);
            if (s == INVALID_SOCKET) {
                s = connect_addr(devices[i].addr, &SONY_TABLE1_UUID, timeout_ms);
            }
            if (s != INVALID_SOCKET) {
                wcsncpy(selected_name, devices[i].name, selected_name_len - 1);
                selected_name[selected_name_len - 1] = L'\0';
                return s;
            }
        }
    }
    return INVALID_SOCKET;
}

static bool parse_hex(const char *text, uint8_t *out, size_t out_cap, size_t *out_len) {
    size_t n = 0;
    int high = -1;

    for (const char *p = text; *p; p++) {
        int v;
        if (*p >= '0' && *p <= '9') v = *p - '0';
        else if (*p >= 'a' && *p <= 'f') v = *p - 'a' + 10;
        else if (*p >= 'A' && *p <= 'F') v = *p - 'A' + 10;
        else continue;

        if (high < 0) {
            high = v;
        } else {
            if (n >= out_cap) return false;
            out[n++] = (uint8_t)((high << 4) | v);
            high = -1;
        }
    }
    if (high >= 0) return false;
    *out_len = n;
    return true;
}

static bool parse_options(int argc, char **argv, options_t *opt) {
    ZeroMemory(opt, sizeof(*opt));
    opt->action = argc > 1 ? argv[1] : "help";
    opt->timeout_ms = 5000;
    opt->data_type = 0x0c;
    opt->ambient_level = 10;
    opt->set_mode = -1;
    opt->set_level = -1;
    opt->set_voice = -1;
    opt->set_auto = -1;
    opt->set_sensitivity = -1;

    int i = 2;
    if (strcmp(opt->action, "raw") == 0) {
        if (i >= argc || !parse_hex(argv[i], opt->raw_payload, sizeof(opt->raw_payload), &opt->raw_len)) {
            fprintf(stderr, "raw requires a hex payload, e.g. xm5ctl raw \"22 00\"\n");
            return false;
        }
        i++;
    } else if (strcmp(opt->action, "batch") == 0) {
        if (i >= argc) {
            fprintf(stderr, "batch requires semicolon-separated hex payloads, e.g. xm5ctl batch \"22 00;66 17\"\n");
            return false;
        }
        opt->batch_text = argv[i++];
    } else if (strcmp(opt->action, "ambient") == 0) {
        if (i < argc && argv[i][0] != '-') {
            opt->ambient_level = atoi(argv[i++]);
        }
        if (opt->ambient_level < 0 || opt->ambient_level > 20) {
            fprintf(stderr, "ambient level must be 0..20\n");
            return false;
        }
    }

    while (i < argc) {
        if (strcmp(argv[i], "--name") == 0 && i + 1 < argc) {
            opt->name_filter = argv[++i];
        } else if (strcmp(argv[i], "--timeout") == 0 && i + 1 < argc) {
            opt->timeout_ms = atoi(argv[++i]);
            if (opt->timeout_ms < 500) opt->timeout_ms = 500;
        } else if (strcmp(argv[i], "--data-type") == 0 && i + 1 < argc) {
            const char *dt = argv[++i];
            if (strcmp(dt, "mdr2") == 0) opt->data_type = 0x0e;
            else if (strcmp(dt, "mdr") == 0) opt->data_type = 0x0c;
            else {
                fprintf(stderr, "unknown data type: %s\n", dt);
                return false;
            }
        } else if (strcmp(argv[i], "--mode") == 0 && i + 1 < argc) {
            const char *m = argv[++i];
            if (strcmp(m, "off") == 0) opt->set_mode = 0;
            else if (strcmp(m, "anc") == 0) opt->set_mode = 1;
            else if (strcmp(m, "ambient") == 0) opt->set_mode = 2;
            else {
                fprintf(stderr, "mode must be anc, ambient or off\n");
                return false;
            }
        } else if (strcmp(argv[i], "--level") == 0 && i + 1 < argc) {
            opt->set_level = atoi(argv[++i]);
            if (opt->set_level < 0 || opt->set_level > 20) {
                fprintf(stderr, "ambient level must be 0..20\n");
                return false;
            }
        } else if (strcmp(argv[i], "--voice") == 0 && i + 1 < argc) {
            const char *v = argv[++i];
            if (strcmp(v, "on") == 0) opt->set_voice = 1;
            else if (strcmp(v, "off") == 0) opt->set_voice = 0;
            else {
                fprintf(stderr, "voice must be on or off\n");
                return false;
            }
        } else if (strcmp(argv[i], "--auto") == 0 && i + 1 < argc) {
            const char *a = argv[++i];
            if (strcmp(a, "on") == 0) opt->set_auto = 1;
            else if (strcmp(a, "off") == 0) opt->set_auto = 0;
            else {
                fprintf(stderr, "auto must be on or off\n");
                return false;
            }
        } else if (strcmp(argv[i], "--sensitivity") == 0 && i + 1 < argc) {
            uint8_t sens;
            if (!sensitivity_value(argv[++i], &sens)) {
                fprintf(stderr, "sensitivity must be low, standard or high\n");
                return false;
            }
            opt->set_sensitivity = (int)sens;
        } else if (strcmp(argv[i], "--hex") == 0) {
            g_hex_dump = true;
        } else if (strcmp(argv[i], "--no-ack") == 0) {
            opt->no_ack = true;
        } else if (strcmp(argv[i], "--ack-only") == 0) {
            opt->ack_only = true;
        } else {
            fprintf(stderr, "unknown option: %s\n", argv[i]);
            return false;
        }
        i++;
    }
    return true;
}

static int invoke_payload(const options_t *opt, const uint8_t *payload, size_t payload_len, uint8_t data_type, int expected_command) {
    SOCKET s;
    wchar_t selected[BLUETOOTH_MAX_NAME_SIZE] = L"";
    mdr_frame_t responses[8];
    int count;

    s = connect_best(opt->name_filter, opt->timeout_ms, selected, ARRAY_LEN(selected));
    if (s == INVALID_SOCKET) {
        fprintf(stderr, "Could not open Sony MDR RFCOMM socket. Is the headset connected in Windows Bluetooth/audio settings?\n");
        return 1;
    }

    wprintf(L"Connecting to %ls, protocol v2...\n", selected[0] ? selected : L"<unnamed>");
    printf("TX %s payload: ", data_type_name(data_type));
    print_hex(payload, payload_len);
    putchar('\n');

    count = send_payload(s, payload, payload_len, data_type, opt->timeout_ms, opt->no_ack, expected_command, responses, (int)ARRAY_LEN(responses));
    closesocket(s);

    if (count < 0) {
        return 1;
    }
    if (expected_command == -2) {
        puts("control accepted (ACK)");
        return 0;
    }
    if (count == 0) {
        puts("No response before timeout.");
        return 0;
    }
    for (int i = 0; i < count; i++) {
        print_known_payload(&responses[i]);
    }
    return 0;
}

static int expected_for_payload(const uint8_t *payload, size_t payload_len) {
    if (payload_len == 0) {
        return -1;
    }
    if (payload[0] == 0x22) {
        return 0x23;
    }
    return (payload[0] + 1) & 0xff;
}

static int invoke_batch(const options_t *opt) {
    SOCKET s;
    wchar_t selected[BLUETOOTH_MAX_NAME_SIZE] = L"";
    char text[4096];
    char *part;
    char *context = NULL;
    int rc = 0;
    int index = 0;

    if (!opt->batch_text || strlen(opt->batch_text) >= sizeof(text)) {
        fprintf(stderr, "batch payload list is empty or too long.\n");
        return 2;
    }
    strcpy(text, opt->batch_text);

    s = connect_best(opt->name_filter, opt->timeout_ms, selected, ARRAY_LEN(selected));
    if (s == INVALID_SOCKET) {
        fprintf(stderr, "Could not open Sony MDR RFCOMM socket. Is the headset connected in Windows Bluetooth/audio settings?\n");
        return 1;
    }

    wprintf(L"Connecting to %ls, protocol v2...\n", selected[0] ? selected : L"<unnamed>");

    for (part = strtok_s(text, ";", &context); part; part = strtok_s(NULL, ";", &context)) {
        uint8_t payload[MAX_PAYLOAD];
        size_t payload_len = 0;
        mdr_frame_t responses[8];
        int expected;
        int count;

        if (!parse_hex(part, payload, sizeof(payload), &payload_len) || payload_len == 0) {
            fprintf(stderr, "invalid batch payload: %s\n", part);
            rc = 2;
            break;
        }

        printf("TX[%d] DATA_MDR payload: ", index + 1);
        print_hex(payload, payload_len);
        putchar('\n');

        expected = expected_for_payload(payload, payload_len);
        count = send_payload_seq(s, payload, payload_len, 0x0c, (uint8_t)(index & 1), opt->timeout_ms,
                                 opt->no_ack, expected, responses, (int)ARRAY_LEN(responses));
        if (count < 0) {
            rc = 1;
            break;
        }
        for (int i = 0; i < count; i++) {
            print_known_payload(&responses[i]);
        }
        index++;
        Sleep(35);
    }

    closesocket(s);
    return rc;
}

/* Ask for one NCASM inquired type and decode the reply to that same type. */
static bool ncasm_query(SOCKET s, uint8_t type, int timeout_ms, uint8_t *seq, ncasm_state_t *out) {
    uint8_t payload[2];
    mdr_frame_t responses[8];
    int count;

    payload[0] = 0x66;
    payload[1] = type;
    count = send_payload_seq(s, payload, sizeof(payload), 0x0c, *seq,
                             timeout_ms, false, 0x67, responses, (int)ARRAY_LEN(responses));
    *seq = (uint8_t)(1u - *seq);
    if (count < 0) {
        return false;
    }
    for (int i = 0; i < count; i++) {
        if (!responses[i].valid) continue;
        if (parse_ncasm(responses[i].payload, responses[i].payload_len, type, out)) {
            return true;
        }
    }
    return false;
}

/*
 * Ask the headset for its NC/ambient state. Newer models (WF-1000XM6) answer
 * inquired type 0x19; older ones answer 0x17 and ignore 0x19 entirely, so try
 * the richer type first and fall back.
 *
 * The fallback needs care, for two reasons.
 *
 * A failed query is not the same as an unsupported one. send_payload_seq
 * reports a link error and a refused ACK the same way, so a single failure is
 * no evidence about which types the model implements - hence each type is
 * tried rather than the first failure ending the probe.
 *
 * And the WF-1000XM6 answers type 0x17 too, with every parameter byte zero.
 * That is indistinguishable from an older model reporting noise control off,
 * so believing it after a 0x19 reply merely went missing would feed an
 * all-zero state into the read-modify-write below and switch the headset off.
 * A 0x19 silence is therefore confirmed with a second attempt before the 0x17
 * answer is accepted.
 */
static bool ncasm_probe(SOCKET s, int timeout_ms, uint8_t *seq, ncasm_state_t *out) {
    ncasm_state_t rich;

    if (ncasm_query(s, 0x19, timeout_ms, seq, out)) {
        return true;
    }
    if (!ncasm_query(s, 0x17, timeout_ms, seq, out)) {
        return false;
    }
    if (ncasm_query(s, 0x19, timeout_ms, seq, &rich)) {
        *out = rich;
        return true;
    }
    return true;
}

static int invoke_ncasm_get(const options_t *opt) {
    SOCKET s;
    wchar_t selected[BLUETOOTH_MAX_NAME_SIZE] = L"";
    ncasm_state_t st;
    uint8_t seq = 0;

    s = connect_best(opt->name_filter, opt->timeout_ms, selected, ARRAY_LEN(selected));
    if (s == INVALID_SOCKET) {
        fprintf(stderr, "Could not open Sony MDR RFCOMM socket. Is the headset connected in Windows Bluetooth/audio settings?\n");
        return 1;
    }
    wprintf(L"Connecting to %ls, protocol v2...\n", selected[0] ? selected : L"<unnamed>");

    if (!ncasm_probe(s, opt->timeout_ms, &seq, &st)) {
        closesocket(s);
        puts("ncasm: no supported noise control state reported.");
        return 1;
    }
    closesocket(s);
    print_ncasm_state(&st);
    return 0;
}

/*
 * Read the current NC/ambient state, apply only the fields the caller asked
 * for, and write the whole block back. The headset stores every field in one
 * command, so a partial write silently clears anything left out - that is how
 * a plain mode change used to switch auto ambient off.
 */
static int invoke_ncasm_set(const options_t *opt) {
    SOCKET s;
    wchar_t selected[BLUETOOTH_MAX_NAME_SIZE] = L"";
    ncasm_state_t st;
    uint8_t payload[9];
    size_t payload_len;
    mdr_frame_t responses[8];
    int count;
    uint8_t seq = 0;

    s = connect_best(opt->name_filter, opt->timeout_ms, selected, ARRAY_LEN(selected));
    if (s == INVALID_SOCKET) {
        fprintf(stderr, "Could not open Sony MDR RFCOMM socket. Is the headset connected in Windows Bluetooth/audio settings?\n");
        return 1;
    }
    wprintf(L"Connecting to %ls, protocol v2...\n", selected[0] ? selected : L"<unnamed>");

    if (!ncasm_probe(s, opt->timeout_ms, &seq, &st)) {
        closesocket(s);
        fprintf(stderr, "Headset did not report a supported noise control state.\n");
        return 1;
    }

    if (opt->set_mode == 0) {
        st.master = 0;
    } else if (opt->set_mode == 1) {
        st.master = 1;
        st.mode = 0;
    } else if (opt->set_mode == 2) {
        st.master = 1;
        st.mode = 1;
    }
    if (opt->set_level >= 0) st.level = (uint8_t)opt->set_level;
    if (opt->set_voice >= 0) st.voice = (uint8_t)opt->set_voice;
    if (st.has_auto) {
        if (opt->set_auto >= 0) st.asm_auto = (uint8_t)opt->set_auto;
        if (opt->set_sensitivity >= 0) st.sensitivity = (uint8_t)opt->set_sensitivity;
    } else if (opt->set_auto >= 0 || opt->set_sensitivity >= 0) {
        fprintf(stderr, "Headset does not support auto ambient sound; ignoring --auto/--sensitivity.\n");
    }

    payload[0] = 0x68;
    payload[1] = st.type;
    payload[2] = 0x01;
    payload[3] = st.master;
    payload[4] = st.mode;
    payload[5] = st.voice;
    payload[6] = st.level;
    payload_len = 7;
    if (st.has_auto) {
        payload[7] = st.asm_auto;
        payload[8] = st.sensitivity;
        payload_len = 9;
    }

    printf("TX DATA_MDR payload: ");
    print_hex(payload, payload_len);
    putchar('\n');

    count = send_payload_seq(s, payload, payload_len, 0x0c, seq, opt->timeout_ms, opt->no_ack,
                             0x69, responses, (int)ARRAY_LEN(responses));
    closesocket(s);
    if (count < 0) {
        return 1;
    }
    for (int i = 0; i < count; i++) {
        print_known_payload(&responses[i]);
    }
    if (count == 0) {
        puts("control accepted (ACK)");
    }
    return 0;
}

static int invoke_listen(const options_t *opt) {
    SOCKET s;
    wchar_t selected[BLUETOOTH_MAX_NAME_SIZE] = L"";
    parser_t parser = { 0 };
    DWORD start;

    s = connect_best(opt->name_filter, 4000, selected, ARRAY_LEN(selected));
    if (s == INVALID_SOCKET) {
        fprintf(stderr, "Could not open Sony MDR RFCOMM socket. Is the headset connected in Windows Bluetooth/audio settings?\n");
        return 1;
    }

    wprintf(L"Listening on %ls for %d ms...\n", selected[0] ? selected : L"<unnamed>", opt->timeout_ms);
    fflush(stdout);

    start = GetTickCount();
    while ((int)(GetTickCount() - start) < opt->timeout_ms) {
        mdr_frame_t rx[8];
        int count = recv_frames(s, &parser, 250, rx, (int)ARRAY_LEN(rx));
        if (count < 0) break;
        for (int i = 0; i < count; i++) {
            if (rx[i].valid && rx[i].data_type == 0x01) continue;
            if (rx[i].valid && rx[i].ack_required) send_ack(s, rx[i].sequence);
            printf("[%5u ms] ", (unsigned)(GetTickCount() - start));
            print_known_payload(&rx[i]);
            fflush(stdout);
        }
    }

    closesocket(s);
    return 0;
}

static int invoke_sequence(const options_t *opt, const uint8_t *first, size_t first_len,
                           const uint8_t *second, size_t second_len, const char *label) {
    SOCKET s;
    wchar_t selected[BLUETOOTH_MAX_NAME_SIZE] = L"";
    mdr_frame_t responses[8];
    int count;

    s = connect_best(opt->name_filter, opt->timeout_ms, selected, ARRAY_LEN(selected));
    if (s == INVALID_SOCKET) {
        fprintf(stderr, "Could not open Sony MDR RFCOMM socket. Is the headset connected in Windows Bluetooth/audio settings?\n");
        return 1;
    }

    wprintf(L"Connecting to %ls, protocol v2...\n", selected[0] ? selected : L"<unnamed>");
    printf("TX DATA_MDR payload: ");
    print_hex(first, first_len);
    putchar('\n');
    count = send_payload_seq(s, first, first_len, 0x0c, 0, opt->timeout_ms, opt->no_ack, -2, responses, (int)ARRAY_LEN(responses));
    if (count < 0) {
        closesocket(s);
        return 1;
    }

    Sleep(120);

    printf("TX DATA_MDR payload: ");
    print_hex(second, second_len);
    putchar('\n');
    count = send_payload_seq(s, second, second_len, 0x0c, 1, opt->timeout_ms, opt->no_ack, -2, responses, (int)ARRAY_LEN(responses));
    closesocket(s);
    if (count < 0) {
        return 1;
    }

    printf("%s accepted (ACK)\n", label);
    return 0;
}

int main(int argc, char **argv) {
    WSADATA wsa;
    options_t opt;
    int rc = 0;

    if (!parse_options(argc, argv, &opt)) {
        print_usage();
        return 2;
    }

    if (strcmp(opt.action, "help") == 0 || strcmp(opt.action, "--help") == 0 || strcmp(opt.action, "-h") == 0) {
        print_usage();
        return 0;
    }

    if (WSAStartup(MAKEWORD(2, 2), &wsa) != 0) {
        die_wsa("WSAStartup");
        return 1;
    }

    if (strcmp(opt.action, "scan") == 0) {
        list_devices(opt.name_filter);
    } else if (strcmp(opt.action, "battery") == 0) {
        const uint8_t payload[] = { 0x22, 0x00 };
        rc = invoke_payload(&opt, payload, sizeof(payload), 0x0c, 0x23);
    } else if (strcmp(opt.action, "ncasm") == 0) {
        rc = invoke_ncasm_get(&opt);
    } else if (strcmp(opt.action, "ncasm-set") == 0) {
        rc = invoke_ncasm_set(&opt);
    } else if (strcmp(opt.action, "raw") == 0) {
        rc = invoke_payload(&opt, opt.raw_payload, opt.raw_len, opt.data_type, opt.ack_only ? -2 : -1);
    } else if (strcmp(opt.action, "listen") == 0) {
        rc = invoke_listen(&opt);
    } else if (strcmp(opt.action, "batch") == 0) {
        rc = invoke_batch(&opt);
    } else if (strcmp(opt.action, "anc") == 0) {
        opt.set_mode = 1;
        rc = invoke_ncasm_set(&opt);
    } else if (strcmp(opt.action, "ambient") == 0) {
        opt.set_mode = 2;
        if (opt.set_level < 0) opt.set_level = opt.ambient_level;
        rc = invoke_ncasm_set(&opt);
    } else if (strcmp(opt.action, "off") == 0) {
        opt.set_mode = 0;
        rc = invoke_ncasm_set(&opt);
    } else {
        fprintf(stderr, "unknown action: %s\n", opt.action);
        print_usage();
        rc = 2;
    }

    WSACleanup();
    return rc;
}
