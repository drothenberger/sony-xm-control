#define WIN32_LEAN_AND_MEAN
#include <winsock2.h>
#include <ws2tcpip.h>
#include <ws2bth.h>
#include <bluetoothapis.h>
#include <windows.h>
#include <bcrypt.h>
#include <stdio.h>
#include <stdlib.h>
#include <stdint.h>
#include <stdbool.h>
#include <string.h>
#include <wchar.h>
#include <time.h>

#pragma comment(lib, "ws2_32.lib")
#pragma comment(lib, "bthprops.lib")
#pragma comment(lib, "bcrypt.lib")

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
    int timeout_ms;         /* wait for an ack and a reply, once connected */
    int connect_timeout_ms; /* opening the RFCOMM channel, which is far slower */
    const char *batch_text;
    uint8_t raw_payload[MAX_PAYLOAD];
    size_t raw_len;
    uint8_t data_type;
    bool no_ack;
    bool ack_only;
    int ambient_level;
    int samples;      /* soundpressure: readings taken per connection */
    int interval_ms;  /* soundpressure: gap between those readings */
    const char *battery_text; /* soundpressure: battery queries asked alongside */
    int battery_every;        /* soundpressure: readings between those queries */
    int safe_listening_every; /* soundpressure: readings between 52 03 checks, 0 = never */
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
    puts("  xm5ctl soundpressure [--samples N] [--interval MS] [--timeout MS]");
    puts("                       [--battery \"22 01;22 02\"] [--battery-every N]");
    puts("                       [--safe-listening-every N]");
    puts("  xm5ctl sealtest [--timeout MS] [--name TEXT]");
    puts("  xm5ctl listen [--timeout MS] [--hex]");
    puts("  xm5ctl raw \"22 00\" [--data-type mdr|mdr2] [--ack-only] [--no-ack]");
    puts("  xm5ctl batch \"22 00;66 17;E6 01\" [--timeout MS] [--name TEXT]");
    puts("  xm5ctl anc");
    puts("  xm5ctl ambient 12");
    puts("  xm5ctl off");
    puts("  xm5ctl pair HOST CODE   use the earbuds through the Android app on HOST");
    puts("  xm5ctl unpair           stop using the phone");
    puts("");
    puts("  any command also takes [--timeout MS] for replies,");
    puts("                        [--connect-timeout MS] for opening the channel,");
    puts("                        [--direct] to skip the phone after pairing");
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

/* [phone_addr], when not empty, is the address of earbuds the phone holds (see
   invoke_scan): that device is listed as connected, "(through the phone)". */
static void list_devices(const bt_device_t *devices, int count, const char *name_filter, const char *phone_addr) {
    printf("%-6s %-6s %-6s %-17s %ls\n", "Conn", "Auth", "Rem", "Address", L"Name");
    for (int i = 0; i < count; i++) {
        char addr[18];
        bool via_phone;
        if (!wide_contains_ascii(devices[i].name, name_filter)) {
            continue;
        }
        snprintf(addr, sizeof(addr), "%02X:%02X:%02X:%02X:%02X:%02X",
            devices[i].addr.rgBytes[5], devices[i].addr.rgBytes[4], devices[i].addr.rgBytes[3],
            devices[i].addr.rgBytes[2], devices[i].addr.rgBytes[1], devices[i].addr.rgBytes[0]);
        via_phone = !devices[i].connected && phone_addr[0] != '\0' && _stricmp(addr, phone_addr) == 0;
        printf("%-6s %-6s %-6s %s",
            devices[i].connected || via_phone ? "yes" : "no",
            devices[i].authenticated ? "yes" : "no",
            devices[i].remembered ? "yes" : "no",
            addr);
        wprintf(L" %ls%ls\n", devices[i].name, via_phone ? L" (through the phone)" : L"");
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
static bool g_direct = false; /* --direct: never through the phone */
static bool g_via_phone = false; /* connected through the phone, not over Bluetooth */
/* Through the phone, the ACK can take seconds: a dozing phone's Wi-Fi alone took up
   to ~1 s per ping, and over 1.5 s for 4 requests in 70 (2026-10-27). Only the ACK
   gets the longer wait. The reply follows it closely once the link is awake, and
   replies that never come must not cost seconds on every refresh. */
#define PHONE_ACK_TIMEOUT_MS 4000

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

/*
 * The codec is reported as a letter in the device information blob, and it
 * follows the connection that is actually playing rather than a fixed link, so
 * it changes as audio moves between multipoint devices.
 */
static const char *codec_text(char c) {
    switch (c) {
    case 'S': return "SBC";
    case 'A': return "AAC";
    case 'L': return "LDAC";
    case 'C': return "LC3 (LE Audio)";
    default: return NULL;
    }
}

/*
 * Connection quality, also a letter in the device information blob. This is the
 * only field that distinguishes all three settings the headset offers: the
 * NCASM-era "E6 00" query reports 0 both for prioritised sound quality and for
 * low latency, so it cannot express the latter at all.
 */
static const char *conn_quality_text(char c) {
    switch (c) {
    case 'S': return "quality";
    case 'C': return "stability";
    case '-': return "low-latency";
    default: return NULL;
    }
}

/* Read the one-letter value of "<key>":"X" out of the JSON blob. */
static bool json_letter(const uint8_t *json, size_t len, const char *key, char *out) {
    size_t key_len = strlen(key);

    for (size_t i = 0; i + key_len < len; i++) {
        if (memcmp(json + i, key, key_len) == 0) {
            *out = (char)json[i + key_len];
            return true;
        }
    }
    return false;
}

static void print_device_info(const uint8_t *json, size_t len) {
    char value;

    if (json_letter(json, len, "\"codec\":\"", &value)) {
        const char *name = codec_text(value);
        if (name) {
            printf("codec: %s\n", name);
        } else {
            printf("codec: %c\n", value);
        }
    } else {
        puts("codec: unknown");
    }

    if (json_letter(json, len, "\"cntQlty\":\"", &value)) {
        const char *name = conn_quality_text(value);
        if (name) {
            printf("connection quality: %s\n", name);
        } else {
            printf("connection quality: %c\n", value);
        }
    }
}

/*
 * 53 03 answers 52 03. The two bytes after the inquired type are both zero
 * while Safe Listening is switched off in Sound Connect and nonzero while it is
 * on (03 03 seen, and 01 03 / 01 01 in older captures), whatever Listening
 * History is set to. The rest is undecoded listening records. It matters
 * because with Safe Listening off the meter does not go quiet: 5A 03 keeps
 * answering with one frozen level, marked valid.
 */
static void print_safe_listening(const uint8_t *p) {
    printf("safe listening: %s\n", p[2] == 0 && p[3] == 0 ? "off" : "on");
}

/*
 * Live sound pressure: the dB level of the audio playing through the headset,
 * measured after the volume stage and ignoring noise cancelling. Status 0x03
 * marks a real reading; anything else means the headset has none to give,
 * which is what it reports whenever nothing is playing and for the first few
 * seconds after playback starts. That is "no reading", not zero, so it must
 * never be rendered as 0 dB.
 */
static void print_sound_pressure(const uint8_t *p, size_t n) {
    if (n < 4 || p[3] != 0x03 || p[2] == 0xff) {
        puts("sound pressure: none");
        return;
    }
    printf("sound pressure: %u dB\n", p[2]);
}

/* The local adapter's address, so the paired-device list can say which entry is
   this PC. Zero when it cannot be read; every comparison then simply fails. */
static uint64_t local_radio_address(void) {
    static uint64_t cached = 0;
    static bool tried = false;
    BLUETOOTH_FIND_RADIO_PARAMS params;
    HANDLE radio = NULL;
    HBLUETOOTH_RADIO_FIND find;

    if (tried) return cached;
    tried = true;

    ZeroMemory(&params, sizeof(params));
    params.dwSize = sizeof(params);
    find = BluetoothFindFirstRadio(&params, &radio);
    if (find) {
        BLUETOOTH_RADIO_INFO info;
        ZeroMemory(&info, sizeof(info));
        info.dwSize = sizeof(info);
        if (BluetoothGetRadioInfo(radio, &info) == ERROR_SUCCESS) {
            cached = info.address.ullLong;
        }
        CloseHandle(radio);
        BluetoothFindRadioClose(find);
    }
    return cached;
}

static bool parse_addr_text(const char *text, uint64_t *out) {
    uint64_t value = 0;
    for (int i = 0; i < 17; i++) {
        char c = text[i];
        if (i % 3 == 2) {
            if (c != ':') return false;
            continue;
        }
        int v;
        if (c >= '0' && c <= '9') v = c - '0';
        else if (c >= 'a' && c <= 'f') v = c - 'a' + 10;
        else if (c >= 'A' && c <= 'F') v = c - 'A' + 10;
        else return false;
        value = (value << 4) | (uint64_t)v;
    }
    *out = value;
    return true;
}

/*
 * The paired-device list, 37 02 in reply to 36 02 and pushed unsolicited as
 * 39 02 whenever the list or the active device changes. Each record is a
 * 17-character ASCII address, a connection slot, a three-byte class of device
 * (FF FF FF when the device is not connected), then a length-prefixed name.
 * The byte after the last record is the slot of the device currently playing.
 * Each side receives the list with itself first, so position means nothing.
 */
static void print_device_list(const uint8_t *p, size_t n) {
    size_t i = 3;
    uint8_t count;
    uint64_t local = local_radio_address();

    if (n < 4) {
        puts("device list: malformed response");
        return;
    }
    count = p[2];
    for (uint8_t entry = 0; entry < count; entry++) {
        char addr[18];
        uint8_t slot;
        bool connected;
        uint8_t name_len;
        uint64_t addr_value = 0;

        if (i + 22 > n) {
            puts("device list: truncated response");
            return;
        }
        memcpy(addr, p + i, 17);
        addr[17] = '\0';
        i += 17;
        slot = p[i];
        connected = !(p[i + 1] == 0xff && p[i + 2] == 0xff && p[i + 3] == 0xff);
        i += 4;
        name_len = p[i++];
        if (i + name_len > n) {
            puts("device list: truncated response");
            return;
        }
        printf("device: slot %u; addr %s; %s", slot, addr, connected ? "connected" : "not connected");
        if (local != 0 && parse_addr_text(addr, &addr_value) && addr_value == local) {
            printf("; this pc");
        }
        printf("; name %.*s\n", (int)name_len, (const char *)(p + i));
        i += name_len;
    }
    if (i < n) {
        printf("device list: %u known; active slot %u\n", count, p[i]);
    } else {
        printf("device list: %u known; active slot unknown\n", count);
    }
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

    /*
     * FE 01 comes from the Android app's PC link, not from the earbuds: the
     * phone saying a setting changed there and is worth reading again. The
     * earbuds' own frames all start with an odd byte, so this one cannot be
     * mistaken for a reply that is being waited for.
     */
    if (p[0] == 0xfe && n == 2 && p[1] == 0x01 && frame->data_type == 0x0c) {
        puts("phone: changed");
        return;
    }

    /*
     * 0x5b covers a whole family; inquired type 0x03 is the sound pressure
     * meter and 0x00 is the equalizer band layout, which the caller reads off
     * the raw payload line below.
     */
    if (p[0] == 0x5b && n >= 2 && p[1] == 0x03) {
        print_sound_pressure(p, n);
        return;
    }
    if (p[0] == 0x53 && n >= 4 && p[1] == 0x03 && frame->data_type == 0x0e) {
        print_safe_listening(p);
        return;
    }

    /* 37 02 is the answer to 36 02; 39 02 is the same list pushed unasked. */
    if ((p[0] == 0x37 || p[0] == 0x39) && n >= 2 && p[1] == 0x02) {
        print_device_list(p, n);
        return;
    }

    switch (p[0]) {
    case 0x11:
    case 0x13:
    case 0x23:
    case 0x25:
        /*
         * 0x13 is also the reply to the device information request (12 00),
         * which carries a JSON blob rather than battery levels. Without this the
         * opening brace and quote decode as a 123% battery.
         */
        if (p[0] == 0x13 && n >= 4 && p[1] == 0x00 && p[2] == '{') {
            print_device_info(p + 2, n - 2);
            return;
        }
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
    int ack_timeout_ms = g_via_phone && timeout_ms < PHONE_ACK_TIMEOUT_MS ? PHONE_ACK_TIMEOUT_MS : timeout_ms;

    if (!build_frame(data_type, tx_sequence, payload, payload_len, tx, sizeof(tx), &tx_len)) {
        fprintf(stderr, "Could not build frame.\n");
        return -1;
    }
    if (!send_all(s, tx, tx_len)) {
        die_wsa("send");
        return -1;
    }

    start = GetTickCount();
    while (!acked && (int)(GetTickCount() - start) < ack_timeout_ms) {
        int remaining = ack_timeout_ms - (int)(GetTickCount() - start);
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

/* WSAEADDRINUSE from the connect means the headset's control session is already
   taken - by Sound Connect on a phone, or by another program on this PC - which
   needs a different fix from a headset that is not there at all. */
static bool connect_in_use;

static void note_connect_error(int err) {
    if (err == WSAEADDRINUSE) connect_in_use = true;
}

static void report_open_failure(void) {
    if (connect_in_use) {
        fprintf(stderr, "Could not open Sony MDR RFCOMM socket: the headset control connection is in use by another app. "
                        "Close Sound Connect or any other headset utility (such as Bluetooth Battery Monitor) and try again.\n");
    } else {
        fprintf(stderr, "Could not open Sony MDR RFCOMM socket. Is the headset connected in Windows Bluetooth/audio settings?\n");
    }
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
                    note_connect_error(so_error);
                    closesocket(s);
                    return INVALID_SOCKET;
                }
            } else {
                /* A refused connect lands here, flagged in exceptfds. */
                int so_error = 0;
                int so_error_len = sizeof(so_error);
                if (rc > 0 && getsockopt(s, SOL_SOCKET, SO_ERROR, (char *)&so_error, &so_error_len) != SOCKET_ERROR) {
                    note_connect_error(so_error);
                }
                closesocket(s);
                return INVALID_SOCKET;
            }
        } else {
            note_connect_error(err);
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

/* Through the phone. The Android app (android-xm-control, docs/pc-phone-cooperation.md)
   can share its own session with the earbuds: it listens on TCP port 47390 and, after
   a one-line handshake, behaves exactly like the earbuds' RFCOMM channel - same frames,
   same ACKs. So everything above works on the TCP socket unchanged, and the phone and
   this PC can both use the earbuds at once. Paired with "xm5ctl pair HOST CODE", which
   stores the phone's name and a key in %APPDATA%\xm5ctl\phone.txt. */

#define PHONE_PORT "47390"
/* Generous: the phone's Wi-Fi can take most of a second to wake, and falling back
   to Bluetooth while the phone holds the earbuds takes them from it. */
#define PHONE_CONNECT_MS 3000
/* After the phone could not be reached, go straight to Bluetooth for this long, so a
   phone that is away does not slow every job by PHONE_CONNECT_MS. */
#define PHONE_SKIP_SECONDS 30
/* How long to keep trying a phone that was there within PHONE_RECENT_SECONDS, before
   falling back to Bluetooth; see connect_phone. */
#define PHONE_RETRY_MS 20000
#define PHONE_RETRY_GAP_MS 2000
#define PHONE_RECENT_SECONDS 60

/* The earbuds' address from the phone's last "OK", or empty. */
static char g_phone_earbuds[32];

typedef enum {
    PHONE_OK,
    PHONE_UNREACHABLE, /* not listening, or not there: use Bluetooth */
    PHONE_NO_EARBUDS,  /* the phone can't reach them either: out of range, or its Bluetooth off */
    PHONE_REFUSED      /* wrong key, or not paired any more */
} phone_result_t;

static bool phone_file(char *path, size_t len, const char *name) {
    const char *appdata = getenv("APPDATA");
    if (appdata == NULL) return false;
    snprintf(path, len, "%s\\xm5ctl", appdata);
    CreateDirectoryA(path, NULL);
    snprintf(path, len, "%s\\xm5ctl\\%s", appdata, name);
    return true;
}

static bool hex_to_bytes(const char *hex, uint8_t *out, size_t out_len) {
    if (strlen(hex) < 2 * out_len) return false;
    for (size_t i = 0; i < out_len; i++) {
        unsigned int b;
        if (sscanf(hex + 2 * i, "%2x", &b) != 1) return false;
        out[i] = (uint8_t)b;
    }
    return true;
}

static void bytes_to_hex(const uint8_t *bytes, size_t len, char *out) {
    for (size_t i = 0; i < len; i++) sprintf(out + 2 * i, "%02x", bytes[i]);
}

static bool load_phone(char *host, size_t host_len, uint8_t key[32]) {
    char path[MAX_PATH];
    char key_hex[80];
    FILE *f;
    bool ok;
    if (!phone_file(path, sizeof(path), "phone.txt")) return false;
    f = fopen(path, "r");
    if (f == NULL) return false;
    ok = fgets(host, (int)host_len, f) != NULL && fgets(key_hex, sizeof(key_hex), f) != NULL;
    fclose(f);
    if (!ok) return false;
    host[strcspn(host, "\r\n")] = '\0';
    return host[0] != '\0' && hex_to_bytes(key_hex, key, 32);
}

static bool hmac_sha256(const uint8_t *secret, size_t secret_len, const uint8_t *data, size_t data_len, uint8_t out[32]) {
    BCRYPT_ALG_HANDLE alg = NULL;
    BCRYPT_HASH_HANDLE hash = NULL;
    bool ok = BCryptOpenAlgorithmProvider(&alg, BCRYPT_SHA256_ALGORITHM, NULL, BCRYPT_ALG_HANDLE_HMAC_FLAG) == 0 &&
              BCryptCreateHash(alg, &hash, NULL, 0, (PUCHAR)secret, (ULONG)secret_len, 0) == 0 &&
              BCryptHashData(hash, (PUCHAR)data, (ULONG)data_len, 0) == 0 &&
              BCryptFinishHash(hash, out, 32, 0) == 0;
    if (hash) BCryptDestroyHash(hash);
    if (alg) BCryptCloseAlgorithmProvider(alg, 0);
    return ok;
}

static SOCKET tcp_connect(const char *host, int connect_ms, int io_ms) {
    struct addrinfo hints;
    struct addrinfo *found = NULL;
    SOCKET s = INVALID_SOCKET;

    ZeroMemory(&hints, sizeof(hints));
    hints.ai_family = AF_INET;
    hints.ai_socktype = SOCK_STREAM;
    hints.ai_protocol = IPPROTO_TCP;
    if (getaddrinfo(host, PHONE_PORT, &hints, &found) != 0) return INVALID_SOCKET;
    for (struct addrinfo *a = found; a != NULL && s == INVALID_SOCKET; a = a->ai_next) {
        u_long nonblocking = 1;
        u_long blocking = 0;
        fd_set writefds;
        fd_set exceptfds;
        struct timeval tv;
        DWORD ms = (DWORD)io_ms;
        BOOL nodelay = TRUE;

        s = socket(a->ai_family, a->ai_socktype, a->ai_protocol);
        if (s == INVALID_SOCKET) continue;
        ioctlsocket(s, FIONBIO, &nonblocking);
        connect(s, a->ai_addr, (int)a->ai_addrlen);
        FD_ZERO(&writefds);
        FD_ZERO(&exceptfds);
        FD_SET(s, &writefds);
        FD_SET(s, &exceptfds);
        tv.tv_sec = connect_ms / 1000;
        tv.tv_usec = (connect_ms % 1000) * 1000;
        if (select(0, NULL, &writefds, &exceptfds, &tv) <= 0 || !FD_ISSET(s, &writefds)) {
            closesocket(s);
            s = INVALID_SOCKET;
            continue;
        }
        ioctlsocket(s, FIONBIO, &blocking);
        setsockopt(s, SOL_SOCKET, SO_RCVTIMEO, (const char *)&ms, sizeof(ms));
        setsockopt(s, SOL_SOCKET, SO_SNDTIMEO, (const char *)&ms, sizeof(ms));
        setsockopt(s, IPPROTO_TCP, TCP_NODELAY, (const char *)&nodelay, sizeof(nodelay));
    }
    freeaddrinfo(found);
    return s;
}

/* One handshake line, a byte at a time so none of the frames after it are taken. */
static bool read_phone_line(SOCKET s, char *line, size_t len) {
    size_t n = 0;
    while (n + 1 < len) {
        char c;
        if (recv(s, &c, 1, 0) != 1) return false;
        if (c == '\n') break;
        if (c != '\r') line[n++] = c;
    }
    line[n] = '\0';
    return true;
}

/* Connects and proves this PC to the phone with [secret]: the stored key for "AUTH",
   or the code the phone shows for "PAIR". A KEY line, which only follows PAIR, is
   copied to [key_hex_out]. Returns the socket ready for frames, or INVALID_SOCKET. */
static SOCKET phone_handshake(const char *host, const char *kind, const uint8_t *secret, size_t secret_len,
                              int io_ms, char *key_hex_out, phone_result_t *result) {
    char line[200];
    uint8_t nonce[16];
    uint8_t proof[32];
    char proof_hex[65];
    SOCKET s = tcp_connect(host, PHONE_CONNECT_MS, io_ms);

    *result = PHONE_UNREACHABLE;
    if (s == INVALID_SOCKET) return INVALID_SOCKET;
    if (!read_phone_line(s, line, sizeof(line)) || strncmp(line, "XMCTL1 ", 7) != 0 ||
        !hex_to_bytes(line + 7, nonce, sizeof(nonce)) || !hmac_sha256(secret, secret_len, nonce, sizeof(nonce), proof)) {
        closesocket(s);
        return INVALID_SOCKET;
    }
    bytes_to_hex(proof, sizeof(proof), proof_hex);
    snprintf(line, sizeof(line), "%s %s\n", kind, proof_hex);
    if (!send_all(s, (const uint8_t *)line, strlen(line))) {
        closesocket(s);
        return INVALID_SOCKET;
    }
    while (read_phone_line(s, line, sizeof(line))) {
        if (strncmp(line, "KEY ", 4) == 0 && key_hex_out != NULL) {
            strncpy(key_hex_out, line + 4, 64);
            key_hex_out[64] = '\0';
        } else if (strcmp(line, "OK") == 0 || strncmp(line, "OK ", 3) == 0) {
            /* "OK <address>": the earbuds the phone holds, for invoke_scan. */
            snprintf(g_phone_earbuds, sizeof(g_phone_earbuds), "%s", line[2] ? line + 3 : "");
            *result = PHONE_OK;
            return s;
        } else if (strncmp(line, "NO ", 3) == 0) {
            fprintf(stderr, "The phone refused: %s\n", line + 3);
            *result = strcmp(line + 3, "earbuds not connected") == 0 ? PHONE_NO_EARBUDS : PHONE_REFUSED;
            break;
        }
    }
    closesocket(s);
    return INVALID_SOCKET;
}

/* A time in seconds kept in a small file next to phone.txt; 0 when there is none. */
static long long read_phone_time(const char *name) {
    char path[MAX_PATH];
    FILE *f;
    long long t = 0;
    if (!phone_file(path, sizeof(path), name)) return 0;
    f = fopen(path, "r");
    if (f == NULL) return 0;
    if (fscanf(f, "%lld", &t) != 1) t = 0;
    fclose(f);
    return t;
}

static void write_phone_time(const char *name, long long t) {
    char path[MAX_PATH];
    FILE *f;
    if (!phone_file(path, sizeof(path), name)) return;
    f = fopen(path, "w");
    if (f == NULL) return;
    fprintf(f, "%lld\n", t);
    fclose(f);
}

static bool phone_skipped(void) {
    return (long long)time(NULL) < read_phone_time("phone-skip.txt");
}

static void skip_phone(void) {
    write_phone_time("phone-skip.txt", (long long)time(NULL) + PHONE_SKIP_SECONDS);
}

/* Called as a job through the phone ends, however it ends. When the phone app
   restarts (an update, Android ending it), the job running through it - usually the
   tray meter's stream - ends, and the next one starts within seconds. */
static void note_phone_seen(void) {
    write_phone_time("phone-seen.txt", (long long)time(NULL));
}

/* Through the phone when paired and not skipped; INVALID_SOCKET means use Bluetooth.
   The phone may connect to the earbuds itself before answering, so the handshake
   waits as long as an RFCOMM connect.

   Falling back while the phone holds the earbuds takes them from it, and it takes
   them back: the two then pull them back and forth until the phone pauses itself. So
   a phone that was there moments ago (PHONE_RECENT_SECONDS) is retried for up to
   PHONE_RETRY_MS first. Restarting, it stops listening for about 10 s. A phone
   that is really gone costs that wait once or twice, then the skip takes over. */
static SOCKET connect_phone(int connect_timeout_ms) {
    char host[256];
    uint8_t key[32];
    phone_result_t result;
    SOCKET s;
    bool recent;
    DWORD start = GetTickCount();

    if (g_direct || !load_phone(host, sizeof(host), key) || phone_skipped()) return INVALID_SOCKET;
    recent = (long long)time(NULL) - read_phone_time("phone-seen.txt") < PHONE_RECENT_SECONDS;
    for (;;) {
        s = phone_handshake(host, "AUTH", key, sizeof(key), connect_timeout_ms, NULL, &result);
        if (result == PHONE_OK) {
            note_phone_seen();
            return s;
        }
        if (!recent || result == PHONE_REFUSED || (int)(GetTickCount() - start) >= PHONE_RETRY_MS) break;
        Sleep(PHONE_RETRY_GAP_MS);
    }
    /* Whatever the reason, Bluetooth next. When the phone has no earbuds it has
       already tried to connect to them and failed, so they are free; once it reaches
       them again it takes them back, and the next job here goes through it. */
    if (result == PHONE_UNREACHABLE) fprintf(stderr, "The phone did not answer; using Bluetooth.\n");
    if (result == PHONE_NO_EARBUDS) fprintf(stderr, "The phone has no connection to the earbuds; using Bluetooth.\n");
    skip_phone();
    return INVALID_SOCKET;
}

static int invoke_pair(const char *host, const char *code) {
    char key_hex[65] = "";
    char path[MAX_PATH];
    uint8_t key[32];
    phone_result_t result;
    FILE *f;
    SOCKET s = phone_handshake(host, "PAIR", (const uint8_t *)code, strlen(code), 15000, key_hex, &result);

    if (s != INVALID_SOCKET) closesocket(s);
    if (result == PHONE_UNREACHABLE && key_hex[0] == '\0') {
        fprintf(stderr, "Could not reach the phone at %s, port %s.\n", host, PHONE_PORT);
        return 1;
    }
    /* A KEY line means paired, even if the phone then has no earbuds to offer. */
    if (!hex_to_bytes(key_hex, key, sizeof(key))) return 1;
    if (!phone_file(path, sizeof(path), "phone.txt") || (f = fopen(path, "w")) == NULL) {
        fprintf(stderr, "Could not save the pairing.\n");
        return 1;
    }
    fprintf(f, "%s\n%s\n", host, key_hex);
    fclose(f);
    if (phone_file(path, sizeof(path), "phone-skip.txt")) DeleteFileA(path);
    printf("paired with %s\n", host);
    return 0;
}

/* Lists the paired devices. When the earbuds are not connected to this PC but the
   phone holds them, they are listed as connected "(through the phone)": the UI decides
   from the scan whether there is a headset to talk to, and through the phone there is,
   even while it plays nothing from this PC.

   Which device the earbuds are comes from the phone ("OK <address>"), remembered in
   earbuds.txt so that the phone is only asked while they are not connected here. The
   address matters: "is anything connected" would be answered by the phone itself,
   which is usually connected to this PC too. */
static void invoke_scan(const char *name_filter) {
    bt_device_t devices[MAX_DEVICES];
    int count = enumerate_devices(devices, MAX_DEVICES);
    char path[MAX_PATH];
    char known[32] = "";
    bool connected_here = false;
    FILE *f;

    if (phone_file(path, sizeof(path), "earbuds.txt") && (f = fopen(path, "r")) != NULL) {
        if (fgets(known, sizeof(known), f) == NULL) known[0] = '\0';
        known[strcspn(known, "\r\n")] = '\0';
        fclose(f);
    }
    for (int i = 0; i < count && known[0] != '\0'; i++) {
        char addr[18];
        snprintf(addr, sizeof(addr), "%02X:%02X:%02X:%02X:%02X:%02X",
            devices[i].addr.rgBytes[5], devices[i].addr.rgBytes[4], devices[i].addr.rgBytes[3],
            devices[i].addr.rgBytes[2], devices[i].addr.rgBytes[1], devices[i].addr.rgBytes[0]);
        if (devices[i].connected && _stricmp(addr, known) == 0) connected_here = true;
    }
    g_phone_earbuds[0] = '\0';
    if (!connected_here) {
        SOCKET s = connect_phone(5000);
        if (s != INVALID_SOCKET) closesocket(s);
        if (g_phone_earbuds[0] != '\0' && _stricmp(g_phone_earbuds, known) != 0 &&
            (f = fopen(path, "w")) != NULL) {
            fprintf(f, "%s\n", g_phone_earbuds);
            fclose(f);
        }
    }
    list_devices(devices, count, name_filter, g_phone_earbuds);
}

static int invoke_unpair(void) {
    char path[MAX_PATH];
    if (phone_file(path, sizeof(path), "phone.txt")) DeleteFileA(path);
    if (phone_file(path, sizeof(path), "phone-skip.txt")) DeleteFileA(path);
    puts("unpaired");
    return 0;
}

static SOCKET connect_best(const char *name_filter, int timeout_ms, wchar_t *selected_name, size_t selected_name_len) {
    bt_device_t devices[MAX_DEVICES];
    int count;
    SOCKET phone = connect_phone(timeout_ms);

    if (phone != INVALID_SOCKET) {
        g_via_phone = true;
        wcsncpy(selected_name, L"the earbuds through the phone", selected_name_len - 1);
        selected_name[selected_name_len - 1] = L'\0';
        return phone;
    }

    count = enumerate_devices(devices, MAX_DEVICES);
    connect_in_use = false;
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
    /*
     * Connecting and waiting for a reply want opposite things, so they get
     * separate budgets.
     *
     * Opening the control channel takes about 280 ms when this PC is the only
     * link to the headset but 1.8 s or more when a phone is connected to it as
     * well, and a value that does not clear that fails every time rather than
     * being merely slow. A successful connect returns as soon as the link is
     * up, so a generous budget here costs nothing except how long a genuine
     * failure takes to report.
     *
     * A reply, once connected, arrives in milliseconds. This budget is only
     * ever spent in full by a command the headset does not answer at all,
     * which is normal: a batch sweeps inquired types that not every model
     * implements. Spending the connect budget on each of those instead turned
     * a sixteen-command batch into eight seconds of held control channel.
     *
     * listen reuses timeout_ms as its capture duration.
     */
    opt->timeout_ms = 900;
    opt->connect_timeout_ms = 5000;
    opt->data_type = 0x0c;
    opt->ambient_level = 10;
    opt->samples = 1;
    opt->interval_ms = 1000;
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
        } else if (strcmp(argv[i], "--connect-timeout") == 0 && i + 1 < argc) {
            opt->connect_timeout_ms = atoi(argv[++i]);
            if (opt->connect_timeout_ms < 500) opt->connect_timeout_ms = 500;
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
        } else if (strcmp(argv[i], "--samples") == 0 && i + 1 < argc) {
            opt->samples = atoi(argv[++i]);
            if (opt->samples < 1) opt->samples = 1;
        } else if (strcmp(argv[i], "--interval") == 0 && i + 1 < argc) {
            opt->interval_ms = atoi(argv[++i]);
            if (opt->interval_ms < 0) opt->interval_ms = 0;
        } else if (strcmp(argv[i], "--battery") == 0 && i + 1 < argc) {
            opt->battery_text = argv[++i];
        } else if (strcmp(argv[i], "--battery-every") == 0 && i + 1 < argc) {
            opt->battery_every = atoi(argv[++i]);
            if (opt->battery_every < 1) opt->battery_every = 1;
        } else if (strcmp(argv[i], "--direct") == 0) {
            g_direct = true;
        } else if (strcmp(argv[i], "--safe-listening-every") == 0 && i + 1 < argc) {
            opt->safe_listening_every = atoi(argv[++i]);
            if (opt->safe_listening_every < 0) opt->safe_listening_every = 0;
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

    s = connect_best(opt->name_filter, opt->connect_timeout_ms, selected, ARRAY_LEN(selected));
    if (s == INVALID_SOCKET) {
        report_open_failure();
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

    s = connect_best(opt->name_filter, opt->connect_timeout_ms, selected, ARRAY_LEN(selected));
    if (s == INVALID_SOCKET) {
        report_open_failure();
        return 1;
    }

    wprintf(L"Connecting to %ls, protocol v2...\n", selected[0] ? selected : L"<unnamed>");

    for (part = strtok_s(text, ";", &context); part; part = strtok_s(NULL, ";", &context)) {
        uint8_t payload[MAX_PAYLOAD];
        size_t payload_len = 0;
        mdr_frame_t responses[8];
        int expected;
        int count;
        /* "mdr2:36 02" sends that entry on DATA_MDR_NO2; the rest go on
           DATA_MDR, which is where all the older reads live. */
        uint8_t data_type = 0x0c;

        while (*part == ' ') part++;
        if (strncmp(part, "mdr2:", 5) == 0) {
            data_type = 0x0e;
            part += 5;
        }

        if (!parse_hex(part, payload, sizeof(payload), &payload_len) || payload_len == 0) {
            fprintf(stderr, "invalid batch payload: %s\n", part);
            rc = 2;
            break;
        }

        printf("TX[%d] %s payload: ", index + 1, data_type_name(data_type));
        print_hex(payload, payload_len);
        putchar('\n');

        expected = expected_for_payload(payload, payload_len);
        count = send_payload_seq(s, payload, payload_len, data_type, (uint8_t)(index & 1), opt->timeout_ms,
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

    s = connect_best(opt->name_filter, opt->connect_timeout_ms, selected, ARRAY_LEN(selected));
    if (s == INVALID_SOCKET) {
        report_open_failure();
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

    s = connect_best(opt->name_filter, opt->connect_timeout_ms, selected, ARRAY_LEN(selected));
    if (s == INVALID_SOCKET) {
        report_open_failure();
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

/*
 * Poll the live sound pressure meter. It answers on DATA_MDR_NO2 rather than
 * DATA_MDR, which is why this cannot ride along in the state batch. Sound
 * Connect reads it about once a second; --samples keeps that cadence on a
 * single connection so a run of readings costs one RFCOMM setup instead of
 * one per reading.
 *
 * --battery asks the given DATA_MDR battery queries on the first reading and
 * every --battery-every readings after that. The data type travels in each
 * frame, not with the connection, so both kinds share the one socket and one
 * alternating sequence bit; on the WF-1000XM6 the replies come back in tens of
 * milliseconds without disturbing the readings. This is what lets something
 * holding the channel for the meter report battery too, rather than a second
 * session having to take the channel for it.
 *
 * --safe-listening-every asks 52 03 the same way, before the first reading and
 * every N readings after, so the caller can tell a frozen level from a live one.
 */
static int invoke_sound_pressure(const options_t *opt) {
    static const uint8_t payload[] = { 0x5a, 0x03 };
    static const uint8_t safe_listening[] = { 0x52, 0x03 };
    uint8_t battery[4][MAX_PAYLOAD];
    size_t battery_len[4];
    int battery_count = 0;
    int battery_every = opt->battery_every > 0 ? opt->battery_every : 60;
    int safe_listening_every = opt->safe_listening_every;
    uint8_t seq = 0;
    SOCKET s;
    wchar_t selected[BLUETOOTH_MAX_NAME_SIZE] = L"";

    if (opt->battery_text) {
        char text[256];
        char *part;
        char *context = NULL;

        if (strlen(opt->battery_text) >= sizeof(text)) {
            fprintf(stderr, "battery payload list is too long.\n");
            return 2;
        }
        strcpy(text, opt->battery_text);
        for (part = strtok_s(text, ";", &context); part; part = strtok_s(NULL, ";", &context)) {
            if (battery_count >= (int)ARRAY_LEN(battery) ||
                !parse_hex(part, battery[battery_count], sizeof(battery[0]), &battery_len[battery_count]) ||
                battery_len[battery_count] == 0) {
                fprintf(stderr, "invalid battery payload: %s\n", part);
                return 2;
            }
            battery_count++;
        }
    }

    s = connect_best(opt->name_filter, opt->connect_timeout_ms, selected, ARRAY_LEN(selected));
    if (s == INVALID_SOCKET) {
        report_open_failure();
        return 1;
    }

    wprintf(L"Connecting to %ls, protocol v2...\n", selected[0] ? selected : L"<unnamed>");

    for (int sample = 0; sample < opt->samples; sample++) {
        mdr_frame_t responses[8];
        int count;

        if (sample > 0 && opt->interval_ms > 0) {
            Sleep((DWORD)opt->interval_ms);
        }

        if (battery_count > 0 && sample % battery_every == 0) {
            for (int b = 0; b < battery_count; b++) {
                count = send_payload_seq(s, battery[b], battery_len[b], 0x0c, seq, opt->timeout_ms, opt->no_ack,
                                         expected_for_payload(battery[b], battery_len[b]),
                                         responses, (int)ARRAY_LEN(responses));
                seq ^= 1;
                /*
                 * A refused query reads the same as a dropped link here. Stop
                 * asking rather than ending the run: the meter is what the
                 * session is for, and a real link failure will end it on the
                 * next reading anyway.
                 */
                if (count < 0) {
                    battery_count = 0;
                    break;
                }
                for (int i = 0; i < count; i++) {
                    print_known_payload(&responses[i]);
                }
            }
        }

        if (safe_listening_every > 0 && sample % safe_listening_every == 0) {
            count = send_payload_seq(s, safe_listening, sizeof(safe_listening), 0x0e, seq,
                                     opt->timeout_ms, opt->no_ack, 0x53, responses, (int)ARRAY_LEN(responses));
            seq ^= 1;
            /* Same reasoning as the battery queries above, and a model that
               never answers would otherwise cost a full timeout each time. */
            if (count <= 0) {
                safe_listening_every = 0;
            }
            for (int i = 0; i < count; i++) {
                print_known_payload(&responses[i]);
            }
        }

        count = send_payload_seq(s, payload, sizeof(payload), 0x0e, seq,
                                 opt->timeout_ms, opt->no_ack, 0x5b, responses, (int)ARRAY_LEN(responses));
        seq ^= 1;
        if (count < 0) {
            closesocket(s);
            return 1;
        }
        if (count == 0) {
            puts("No response before timeout.");
        }
        for (int i = 0; i < count; i++) {
            print_known_payload(&responses[i]);
        }
        fflush(stdout);
    }

    closesocket(s);
    return 0;
}

/*
 * Run the ear tip seal test, which Sound Connect offers as a check of the
 * wearing condition: the earbuds play a tone and judge each side's seal as
 * good or poor. It is the system family's inquired type 06.
 *
 *   F0 06                -> F1 06 <seconds> ...      how long a test takes
 *   F4 06 <mode> <count>                            enter (01 01) or leave (00 00) the test mode
 *   F8 06 00 00 FF FF    -> F9 06 <status> <error>   start; status 01 = started
 *                        <- F9 06 02 ...             pushed when it completes
 *                        <- FD 06 <left> <right>     pushed result; 00 = good, 01 = poor
 *
 * A model without the test acks F0 06 and never answers it. With an earbud out
 * of the ear the start is refused at once: status 00 and an error naming the
 * side. FA 06 reads a result back too, but it still answers with the previous
 * run's result after a refusal, so only the pushed one is trusted here.
 */
static int invoke_seal_test(const options_t *opt) {
    static const uint8_t capability[] = { 0xf0, 0x06 };
    static const uint8_t mode_in[] = { 0xf4, 0x06, 0x01, 0x01 };
    static const uint8_t mode_out[] = { 0xf4, 0x06, 0x00, 0x00 };
    static const uint8_t start_test[] = { 0xf8, 0x06, 0x00, 0x00, 0xff, 0xff };
    mdr_frame_t responses[8];
    parser_t parser = { 0 };
    uint8_t seq = 0;
    int seconds = -1;
    int status = -1;
    int error = 0;
    bool have_result = false;
    uint8_t left = 0;
    uint8_t right = 0;
    int count;
    int rc = 0;
    DWORD start;
    int budget_ms;
    SOCKET s;
    wchar_t selected[BLUETOOTH_MAX_NAME_SIZE] = L"";

    s = connect_best(opt->name_filter, opt->connect_timeout_ms, selected, ARRAY_LEN(selected));
    if (s == INVALID_SOCKET) {
        report_open_failure();
        return 1;
    }

    wprintf(L"Connecting to %ls, protocol v2...\n", selected[0] ? selected : L"<unnamed>");

    count = send_payload_seq(s, capability, sizeof(capability), 0x0c, seq, opt->timeout_ms, opt->no_ack,
                             0xf1, responses, (int)ARRAY_LEN(responses));
    seq ^= 1;
    if (count < 0) {
        closesocket(s);
        return 1;
    }
    for (int i = 0; i < count; i++) {
        const mdr_frame_t *f = &responses[i];
        if (f->valid && f->payload_len >= 3 && f->payload[0] == 0xf1 && f->payload[1] == 0x06) {
            seconds = f->payload[2];
        }
    }
    if (seconds < 0) {
        puts("seal test: unsupported");
        closesocket(s);
        return 0;
    }

    count = send_payload_seq(s, mode_in, sizeof(mode_in), 0x0c, seq, opt->timeout_ms, opt->no_ack,
                             0xf5, responses, (int)ARRAY_LEN(responses));
    seq ^= 1;
    if (count < 0) {
        closesocket(s);
        return 1;
    }

    count = send_payload_seq(s, start_test, sizeof(start_test), 0x0c, seq, opt->timeout_ms, opt->no_ack,
                             0xf9, responses, (int)ARRAY_LEN(responses));
    seq ^= 1;
    if (count < 0) {
        closesocket(s);
        return 1;
    }
    for (int i = 0; i < count; i++) {
        const mdr_frame_t *f = &responses[i];
        if (f->valid && f->payload_len >= 4 && f->payload[0] == 0xf9 && f->payload[1] == 0x06) {
            status = f->payload[2];
            error = f->payload[3];
        }
    }

    if (status == 1) {
        printf("seal test: started; %d s\n", seconds);
        fflush(stdout);

        /* The result follows the completion by a few tens of milliseconds. */
        budget_ms = seconds * 1000 + 5000;
        start = GetTickCount();
        while (!have_result && status != 3 && (int)(GetTickCount() - start) < budget_ms) {
            mdr_frame_t rx[8];
            count = recv_frames(s, &parser, 250, rx, (int)ARRAY_LEN(rx));
            if (count < 0) {
                rc = 1;
                break;
            }
            for (int i = 0; i < count; i++) {
                const mdr_frame_t *f = &rx[i];
                if (!f->valid || f->data_type == 0x01) continue;
                if (f->ack_required) send_ack(s, f->sequence);
                if (f->payload_len >= 4 && f->payload[1] == 0x06) {
                    if (f->payload[0] == 0xf9) {
                        status = f->payload[2];
                        error = f->payload[3];
                    } else if (f->payload[0] == 0xfd && status == 2) {
                        left = f->payload[2];
                        right = f->payload[3];
                        have_result = true;
                    }
                }
            }
        }
    }

    if (rc != 0) {
        /* The link went, and the mode with it. */
        closesocket(s);
        return rc;
    }

    if (have_result) {
        printf("seal test: left %s; right %s\n", left == 0 ? "good" : "poor", right == 0 ? "good" : "poor");
    } else if (status == 0 || status == 3) {
        printf("seal test: %s; error %d\n", status == 0 ? "refused" : "failed", error);
    } else {
        puts("seal test: no result");
    }
    fflush(stdout);

    /* Left in the mode, the earbuds would still be waiting for a test. */
    send_payload_seq(s, mode_out, sizeof(mode_out), 0x0c, seq, opt->timeout_ms, opt->no_ack,
                     0xf5, responses, (int)ARRAY_LEN(responses));

    closesocket(s);
    return 0;
}

static int invoke_listen(const options_t *opt) {
    SOCKET s;
    wchar_t selected[BLUETOOTH_MAX_NAME_SIZE] = L"";
    parser_t parser = { 0 };
    DWORD start;

    s = connect_best(opt->name_filter, opt->connect_timeout_ms, selected, ARRAY_LEN(selected));
    if (s == INVALID_SOCKET) {
        report_open_failure();
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

    s = connect_best(opt->name_filter, opt->connect_timeout_ms, selected, ARRAY_LEN(selected));
    if (s == INVALID_SOCKET) {
        report_open_failure();
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

    if (argc >= 2 && (strcmp(argv[1], "pair") == 0 || strcmp(argv[1], "unpair") == 0)) {
        if (strcmp(argv[1], "pair") == 0 && argc != 4) {
            print_usage();
            return 2;
        }
        if (WSAStartup(MAKEWORD(2, 2), &wsa) != 0) {
            die_wsa("WSAStartup");
            return 1;
        }
        rc = strcmp(argv[1], "pair") == 0 ? invoke_pair(argv[2], argv[3]) : invoke_unpair();
        WSACleanup();
        return rc;
    }

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
        invoke_scan(opt.name_filter);
    } else if (strcmp(opt.action, "battery") == 0) {
        const uint8_t payload[] = { 0x22, 0x00 };
        rc = invoke_payload(&opt, payload, sizeof(payload), 0x0c, 0x23);
    } else if (strcmp(opt.action, "ncasm") == 0) {
        rc = invoke_ncasm_get(&opt);
    } else if (strcmp(opt.action, "ncasm-set") == 0) {
        rc = invoke_ncasm_set(&opt);
    } else if (strcmp(opt.action, "raw") == 0) {
        rc = invoke_payload(&opt, opt.raw_payload, opt.raw_len, opt.data_type, opt.ack_only ? -2 : -1);
    } else if (strcmp(opt.action, "soundpressure") == 0) {
        rc = invoke_sound_pressure(&opt);
    } else if (strcmp(opt.action, "sealtest") == 0) {
        rc = invoke_seal_test(&opt);
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

    if (g_via_phone) note_phone_seen();
    WSACleanup();
    return rc;
}
