// net-meter: this container's flows, from packet headers, on a packet socket in its network namespace.
// Needs CAP_NET_RAW (a file capability on this binary). A kernel filter keeps 128 bytes of each packet; DNS,
// and TCP packets starting a TLS handshake or an HTTP message, are kept whole and only their first fields read.
// Once a second it prints the flows active since the last report, the requests answered, the DNS replies seen,
// and a "." line:
//
//   F <tcp|udp|icmp|ip> <local ip> <local port> <remote ip> <remote port> <bytes out> <bytes in> <packets out> <packets in>
//     <handshake ms|-> <retransmitted out> <retransmitted in> <tls name|-> <http method|-> <http host|-> <http path|->
//     <http status|-> <http response ms|-> <tls version|-> <alpn chosen|-> <alpn offered,...|->
//     <resets out> <resets in> <refused 0|1> <unanswered 0|1> <zero windows out> <zero windows in> <unreachable> <too big>
//   Q <protocol: http, or a name from the protocols table> <client|server> <remote ip> <remote port> <local port> <operation>
//     <requests> <errors> <request bytes> <response bytes> <latency histogram, 16 buckets>
//     (a flow's protocol is read from what it carries, never guessed from its ports; flows it can't be read
//     from get no Q lines)
//   D <name> <NOERROR|NXDOMAIN|...> <answer,...|-> <reply ms|-> <cname,...|->
//   O <bytes out> <bytes in>        (flows that didn't fit in the table)
//
// Counts in F are totals since the flow was first seen, in Q since the last report. Loopback traffic isn't
// counted, but DNS replies on it are read (Docker's resolver answers on 127.0.0.11).
//
// "C <seconds>" on stdin records the packets' first 128 bytes (headers, not contents) for that long, as
// "P <seconds> <microseconds> <length> <hex>" lines, then "C <packets>".

#include <arpa/inet.h>
#include <errno.h>
#include <linux/filter.h>
#include <linux/if_ether.h>
#include <linux/if_packet.h>
#include <net/if.h>
#include <netinet/in.h>
#include <poll.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <strings.h>
#include <sys/socket.h>
#include <time.h>
#include <unistd.h>

#define MAX_FLOWS 8192
#define IDLE_SECONDS 3600 // as long as the dashboard looks back, so totals of a quiet flow don't restart
#define MAX_DNS_PER_REPORT 256
#define MAX_EXCHANGES 512
#define SNAP 128
#define MAX_CAPTURED 200000
#define MAX_BURST 64
#define UNANSWERED_US 3000000

struct flow;

// A protocol whose requests are counted: its name in Q lines, and its decoder (see the protocols table). n is
// what was captured of a payload, len its real length.
struct protocol
{
    const char *name;
    int (*greets)(const uint8_t *p, size_t n, size_t len);   // a server's first words on a connection
    int (*detect)(const uint8_t *p, size_t n, size_t len);   // a message only its clients send
    // 1 a request awaiting a reply, 2 one that gets none, 0 not the start of a request (or the rest of one)
    int (*request)(struct flow *f, const uint8_t *p, size_t n, char *op, size_t op_len);
    // -1 not a reply, 0 a reply, 1 an error; none: any packet from the server answers, never an error
    int (*reply)(struct flow *f, const uint8_t *p, size_t n);
};

// HTTP is read by its own code (read_http_request and read_http_response).
static const struct protocol http = {.name = "http"};

struct flow
{
    uint8_t used, proto, family, active;
    uint8_t local[16], remote[16];
    uint16_t local_port, remote_port;
    uint64_t bytes_out, bytes_in, packets_out, packets_in;
    time_t last_seen;
    uint64_t handshake_us;
    uint32_t rtt_us, next_out, next_in;
    uint8_t connecting, seq_out_set, seq_in_set;
    uint32_t retrans_out, retrans_in;
    char tls_name[64], http_method[8], http_host[48], http_path[64];
    uint64_t request_us;
    uint8_t request_outgoing, client_hello_seen;
    uint16_t http_status;
    uint32_t response_us;
    char tls_version[4], alpn[16], alpn_offered[32];
    uint32_t resets_out, resets_in, zero_window_out, zero_window_in, unreachable, too_big;
    uint8_t refused, unanswered, established;
    // The request in flight: what it is, when it was sent, and which way requests go.
    const struct protocol *protocol;   // none until its traffic shows one
    uint8_t pending, to_server_outgoing;
    uint16_t server_port;
    // How the connection began, if that was seen: who opened it, whether anything was said yet, and the protocols
    // a greeting several share could be (a bit per protocols entry; the client's first command tells which).
    uint8_t opened, opener_outgoing, spoke;
    uint32_t candidates;
    char tag[12];   // IMAP: the request's tag, which its completion repeats
    char operation[48];
    uint64_t pending_us;
    int exchange;   // its line in this report's table, checked before use
};

// Requests answered since the last report, per flow and operation.
struct exchange
{
    struct flow *flow;
    const struct protocol *protocol;
    uint8_t client;
    char operation[48];
    uint32_t requests, errors, histogram[16];
    uint64_t request_bytes, response_bytes;
};

static struct exchange exchanges[MAX_EXCHANGES];
static int exchange_count;
static uint64_t capture_until_us;
static long captured_packets;

static struct flow flows[MAX_FLOWS];
static int flow_count;
static uint64_t overflow_out, overflow_in;
static int overflow_active;

static char dns_lines[MAX_DNS_PER_REPORT][400];
static int dns_count;

#define MAX_PENDING 256
static struct { uint16_t id; uint32_t name_hash; uint64_t sent_us; } pending[MAX_PENDING];
static int pending_next;

static uint64_t now_us(void)
{
    struct timespec t;
    clock_gettime(CLOCK_MONOTONIC, &t);
    return (uint64_t)t.tv_sec * 1000000 + t.tv_nsec / 1000;
}

static uint32_t hash_name(const char *name)
{
    uint32_t h = 2166136261u;
    for (; *name; name++) h = (h ^ (uint8_t)*name) * 16777619u;
    return h;
}

// Offsets are from the IP header (SOCK_DGRAM). A load past the end of a packet drops it, so the TCP payload is
// read only after checking its length. Jumps count the instructions to skip; the targets are on the right.
#define WORD(a, b, c, d) (((uint32_t)(a) << 24) | ((b) << 16) | ((c) << 8) | (d))
static struct sock_filter filter_code[] = {
    /* 0 */ BPF_STMT(BPF_LD | BPF_H | BPF_ABS, SKF_AD_OFF + SKF_AD_PROTOCOL),
    /* 1 */ BPF_JUMP(BPF_JMP | BPF_JEQ | BPF_K, ETH_P_IP, 1, 0),                 // 3, 2
    /* 2 */ BPF_JUMP(BPF_JMP | BPF_JEQ | BPF_K, ETH_P_IPV6, 18, 48),             // 21, 51
    // IPv4
    /* 3 */ BPF_STMT(BPF_LD | BPF_B | BPF_ABS, 9),
    /* 4 */ BPF_JUMP(BPF_JMP | BPF_JEQ | BPF_K, IPPROTO_UDP, 1, 0),              // 6, 5
    /* 5 */ BPF_JUMP(BPF_JMP | BPF_JEQ | BPF_K, IPPROTO_TCP, 9, 44),             // 15, 50
    /* 6 */ BPF_STMT(BPF_LDX | BPF_B | BPF_MSH, 0),                              // X = IP header length
    /* 7 */ BPF_STMT(BPF_LD | BPF_H | BPF_IND, 0),
    /* 8 */ BPF_JUMP(BPF_JMP | BPF_JEQ | BPF_K, 53, 40, 0),                      // 49
    /* 9 */ BPF_STMT(BPF_LD | BPF_H | BPF_IND, 2),
    /* 10 */ BPF_JUMP(BPF_JMP | BPF_JEQ | BPF_K, 53, 38, 0),                     // 49
    /* 11 */ BPF_STMT(BPF_LD | BPF_W | BPF_ABS, 12),
    /* 12 */ BPF_JUMP(BPF_JMP | BPF_JEQ | BPF_K, 0x7f00000b, 36, 0),             // 49
    /* 13 */ BPF_STMT(BPF_LD | BPF_W | BPF_ABS, 16),
    /* 14 */ BPF_JUMP(BPF_JMP | BPF_JEQ | BPF_K, 0x7f00000b, 34, 35),            // 49, 50
    /* 15 */ BPF_STMT(BPF_LDX | BPF_B | BPF_MSH, 0),
    /* 16 */ BPF_STMT(BPF_LD | BPF_B | BPF_IND, 12),                             // TCP data offset
    /* 17 */ BPF_STMT(BPF_ALU | BPF_AND | BPF_K, 0xf0),
    /* 18 */ BPF_STMT(BPF_ALU | BPF_RSH | BPF_K, 2),
    /* 19 */ BPF_STMT(BPF_ALU | BPF_ADD | BPF_X, 0),                             // A = payload offset
    /* 20 */ BPF_STMT(BPF_JMP | BPF_JA, 11),                                     // 32
    // IPv6 (TCP or UDP right after the fixed header)
    /* 21 */ BPF_STMT(BPF_LD | BPF_B | BPF_ABS, 6),
    /* 22 */ BPF_JUMP(BPF_JMP | BPF_JEQ | BPF_K, IPPROTO_UDP, 1, 0),             // 24, 23
    /* 23 */ BPF_JUMP(BPF_JMP | BPF_JEQ | BPF_K, IPPROTO_TCP, 4, 26),            // 28, 50
    /* 24 */ BPF_STMT(BPF_LD | BPF_H | BPF_ABS, 40),
    /* 25 */ BPF_JUMP(BPF_JMP | BPF_JEQ | BPF_K, 53, 23, 0),                     // 49
    /* 26 */ BPF_STMT(BPF_LD | BPF_H | BPF_ABS, 42),
    /* 27 */ BPF_JUMP(BPF_JMP | BPF_JEQ | BPF_K, 53, 21, 22),                    // 49, 50
    /* 28 */ BPF_STMT(BPF_LD | BPF_B | BPF_ABS, 52),
    /* 29 */ BPF_STMT(BPF_ALU | BPF_AND | BPF_K, 0xf0),
    /* 30 */ BPF_STMT(BPF_ALU | BPF_RSH | BPF_K, 2),
    /* 31 */ BPF_STMT(BPF_ALU | BPF_ADD | BPF_K, 40),
    // TCP payload at offset A: at least 4 bytes of it?
    /* 32 */ BPF_STMT(BPF_ST, 0),
    /* 33 */ BPF_STMT(BPF_ALU | BPF_ADD | BPF_K, 4),
    /* 34 */ BPF_STMT(BPF_MISC | BPF_TAX, 0),
    /* 35 */ BPF_STMT(BPF_LD | BPF_W | BPF_LEN, 0),
    /* 36 */ BPF_JUMP(BPF_JMP | BPF_JGE | BPF_X, 0, 0, 13),                      // 37, 50
    /* 37 */ BPF_STMT(BPF_LDX | BPF_MEM, 0),
    /* 38 */ BPF_STMT(BPF_LD | BPF_W | BPF_IND, 0),
    /* 39 */ BPF_JUMP(BPF_JMP | BPF_JEQ | BPF_K, WORD('G', 'E', 'T', ' '), 9, 0), // 49
    /* 40 */ BPF_JUMP(BPF_JMP | BPF_JEQ | BPF_K, WORD('P', 'O', 'S', 'T'), 8, 0),
    /* 41 */ BPF_JUMP(BPF_JMP | BPF_JEQ | BPF_K, WORD('P', 'U', 'T', ' '), 7, 0),
    /* 42 */ BPF_JUMP(BPF_JMP | BPF_JEQ | BPF_K, WORD('H', 'E', 'A', 'D'), 6, 0),
    /* 43 */ BPF_JUMP(BPF_JMP | BPF_JEQ | BPF_K, WORD('D', 'E', 'L', 'E'), 5, 0),
    /* 44 */ BPF_JUMP(BPF_JMP | BPF_JEQ | BPF_K, WORD('P', 'A', 'T', 'C'), 4, 0),
    /* 45 */ BPF_JUMP(BPF_JMP | BPF_JEQ | BPF_K, WORD('O', 'P', 'T', 'I'), 3, 0),
    /* 46 */ BPF_JUMP(BPF_JMP | BPF_JEQ | BPF_K, WORD('H', 'T', 'T', 'P'), 2, 0), // a response
    /* 47 */ BPF_STMT(BPF_ALU | BPF_RSH | BPF_K, 24),
    /* 48 */ BPF_JUMP(BPF_JMP | BPF_JEQ | BPF_K, 0x16, 0, 1),                    // 49, 50
    /* 49 */ BPF_STMT(BPF_RET | BPF_K, 2048),
    /* 50 */ BPF_STMT(BPF_RET | BPF_K, 128),
    /* 51 */ BPF_STMT(BPF_RET | BPF_K, 0),
};

static uint32_t hash(const struct flow *f)
{
    uint32_t h = 2166136261u;
    const uint8_t *parts[] = {f->local, f->remote};
    for (int p = 0; p < 2; p++)
    {
        for (int i = 0; i < 16; i++)
        {
            h = (h ^ parts[p][i]) * 16777619u;
        }
    }

    h = (h ^ f->local_port) * 16777619u;
    h = (h ^ f->remote_port) * 16777619u;
    return (h ^ f->proto) * 16777619u;
}

static int same(const struct flow *a, const struct flow *b)
{
    return a->proto == b->proto && a->local_port == b->local_port && a->remote_port == b->remote_port
        && memcmp(a->local, b->local, 16) == 0 && memcmp(a->remote, b->remote, 16) == 0;
}

static struct flow *find(const struct flow *key)
{
    uint32_t i = hash(key) % MAX_FLOWS;
    for (;;)
    {
        if (!flows[i].used)
        {
            if (flow_count >= MAX_FLOWS * 3 / 4)
            {
                return NULL;
            }

            flows[i] = *key;
            flows[i].used = 1;
            flow_count++;
            return &flows[i];
        }

        if (same(&flows[i], key))
        {
            return &flows[i];
        }

        i = (i + 1) % MAX_FLOWS;
    }
}

static struct flow *count(const struct flow *key, int outgoing, uint64_t bytes, time_t now)
{
    struct flow *f = find(key);
    if (!f)
    {
        if (outgoing) overflow_out += bytes; else overflow_in += bytes;
        overflow_active = 1;
        return NULL;
    }

    if (outgoing) { f->bytes_out += bytes; f->packets_out++; }
    else { f->bytes_in += bytes; f->packets_in++; }
    f->last_seen = now;
    f->active = 1;
    return f;
}

static uint32_t be32(const uint8_t *p) { return ((uint32_t)p[0] << 24) | (p[1] << 16) | (p[2] << 8) | p[3]; }
static unsigned be16(const uint8_t *p) { return (p[0] << 8) | p[1]; }
static uint32_t le32(const uint8_t *p) { return p[0] | (p[1] << 8) | (p[2] << 16) | ((uint32_t)p[3] << 24); }
static unsigned le24(const uint8_t *p) { return p[0] | (p[1] << 8) | ((unsigned)p[2] << 16); }

// A flow already in the table, without adding it.
static struct flow *existing(const struct flow *key)
{
    for (uint32_t i = hash(key) % MAX_FLOWS, n = 0; flows[i].used && n < MAX_FLOWS; i = (i + 1) % MAX_FLOWS, n++)
    {
        if (same(&flows[i], key)) return &flows[i];
    }
    return NULL;
}

static void token(const uint8_t *from, size_t len, const char *stop, char *out, size_t out_len)
{
    size_t n = 0;
    for (size_t i = 0; i < len && n + 1 < out_len; i++)
    {
        if (from[i] <= ' ' || from[i] >= 127 || strchr(stop, from[i])) break;
        out[n++] = (char)from[i];
    }
    out[n] = '\0';
}

static void read_alpn(const uint8_t *p, size_t end, size_t pos, char *out, size_t out_len)
{
    // Extension data: list length (2), then length-prefixed names.
    for (pos += 2; pos < end;)
    {
        size_t name_len = p[pos++];
        if (pos + name_len > end) return;
        size_t used = strlen(out);
        if (used + name_len + 2 > out_len) return;
        if (used) out[used++] = ',';
        token(p + pos, name_len, ",", out + used, out_len - used);
        pos += name_len;
    }
}

static void read_client_hello(struct flow *f, const uint8_t *p, size_t len)
{
    // Record header (5), handshake header (4, type 1), version (2), random (32).
    if (len < 44 || p[0] != 0x16 || p[5] != 0x01) return;
    f->client_hello_seen = 1;
    size_t pos = 43;
    pos += 1 + p[pos];                                  // session id
    if (pos + 2 > len) return;
    pos += 2 + be16(p + pos);                           // cipher suites
    if (pos + 1 > len) return;
    pos += 1 + p[pos];                                  // compression methods
    if (pos + 2 > len) return;
    size_t end = pos + 2 + be16(p + pos);
    if (end > len) end = len;
    for (pos += 2; pos + 4 <= end;)
    {
        unsigned type = be16(p + pos), size = be16(p + pos + 2);
        pos += 4;
        // server_name: list length (2), name type (1, 0 = host name), name length (2), name.
        if (type == 0 && size >= 5 && pos + 5 <= end && p[pos + 2] == 0)
        {
            size_t name_len = be16(p + pos + 3);
            if (pos + 5 + name_len <= end) token(p + pos + 5, name_len, "", f->tls_name, sizeof f->tls_name);
        }
        else if (type == 16 && pos + size <= end)
        {
            f->alpn_offered[0] = '\0';
            read_alpn(p, pos + size, pos, f->alpn_offered, sizeof f->alpn_offered);
        }
        pos += size;
    }
}

// TLS 1.3 encrypts the chosen ALPN; only older versions send it in the clear.
static void read_server_hello(struct flow *f, const uint8_t *p, size_t len)
{
    if (len < 44 || p[0] != 0x16 || p[5] != 0x02) return;
    unsigned version = be16(p + 9);
    size_t pos = 43;
    pos += 1 + p[pos];                                  // session id
    pos += 2 + 1;                                       // cipher suite, compression method
    if (pos + 2 <= len)
    {
        size_t end = pos + 2 + be16(p + pos);
        if (end > len) end = len;
        for (pos += 2; pos + 4 <= end;)
        {
            unsigned type = be16(p + pos), size = be16(p + pos + 2);
            pos += 4;
            if (type == 43 && size == 2 && pos + 2 <= end) version = be16(p + pos);
            else if (type == 16 && pos + size <= end) read_alpn(p, pos + size, pos, f->alpn, sizeof f->alpn);
            pos += size;
        }
    }
    const char *names[] = {"1.0", "1.1", "1.2", "1.3"};
    if (version >= 0x0301 && version <= 0x0304) snprintf(f->tls_version, sizeof f->tls_version, "%s", names[version - 0x0301]);
}

static struct exchange *exchange_of(struct flow *f)
{
    if (f->exchange >= 0 && f->exchange < exchange_count && exchanges[f->exchange].flow == f
        && strcmp(exchanges[f->exchange].operation, f->operation) == 0) return &exchanges[f->exchange];
    for (int i = 0; i < exchange_count; i++)
    {
        if (exchanges[i].flow == f && strcmp(exchanges[i].operation, f->operation) == 0) { f->exchange = i; return &exchanges[i]; }
    }
    if (exchange_count >= MAX_EXCHANGES) return NULL;
    struct exchange *e = &exchanges[exchange_count];
    memset(e, 0, sizeof *e);
    e->flow = f;
    e->protocol = f->protocol;
    e->client = f->to_server_outgoing;
    memcpy(e->operation, f->operation, sizeof e->operation);
    f->exchange = exchange_count++;
    return e;
}

static void request(struct flow *f, int outgoing, const char *operation, size_t bytes)
{
    f->to_server_outgoing = (uint8_t)outgoing;
    snprintf(f->operation, sizeof f->operation, "%s", operation);
    f->pending = 1;
    f->pending_us = now_us();
    struct exchange *e = exchange_of(f);
    if (e) e->request_bytes += bytes;
}

static void response(struct flow *f, int error)
{
    if (!f->pending) return;
    f->pending = 0;
    struct exchange *e = exchange_of(f);
    if (!e) return;
    static const double bounds_ms[15] = {0.1, 0.25, 0.5, 1, 2.5, 5, 10, 25, 50, 100, 250, 500, 1000, 2500, 5000};
    double ms = (now_us() - f->pending_us) / 1000.0;
    int bucket = 0;
    while (bucket < 15 && ms > bounds_ms[bucket]) bucket++;
    e->requests++;
    e->errors += error ? 1 : 0;
    e->histogram[bucket]++;
}

static void first_word(const uint8_t *p, size_t len, char *out, size_t out_len)
{
    size_t start = 0;
    while (start < len && (p[start] == ' ' || p[start] == '\n' || p[start] == '\r' || p[start] == '\t' || p[start] == '(')) start++;
    size_t n = 0;
    for (size_t i = start; i < len && n + 1 < out_len; i++)
    {
        char c = (char)p[i];
        if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || c == '_')) break;
        out[n++] = (char)(c >= 'a' && c <= 'z' ? c - 32 : c);
    }
    out[n] = '\0';
    if (!n) snprintf(out, out_len, "QUERY");
}

// A request that gets no reply (an MQTT publish at QoS 0, an AMQP publish): counted, without a latency.
static void fire(struct flow *f, int outgoing, const char *operation, size_t bytes)
{
    request(f, outgoing, operation, bytes);
    f->pending = 0;
    struct exchange *e = exchange_of(f);
    if (e) e->requests++;
}

// A text protocol's command, if it is one of the known ones: the first word, upper-cased.
static int command(const uint8_t *p, size_t len, const char *const *known, char *out, size_t out_len)
{
    first_word(p, len, out, out_len);
    for (; *known; known++) if (strcmp(out, *known) == 0) return 1;
    out[0] = '\0';
    return 0;
}

// A three-digit reply code at the start of a line (SMTP, FTP), or -1.
static int reply_code(const uint8_t *p, size_t len)
{
    if (len < 3 || p[0] < '1' || p[0] > '5' || p[1] < '0' || p[1] > '9' || p[2] < '0' || p[2] > '9') return -1;
    return (p[0] - '0') * 100 + (p[1] - '0') * 10 + (p[2] - '0');
}

// A BER length (LDAP), short or long form; -1 when cut off.
static long ber_length(const uint8_t *p, size_t len, size_t *at)
{
    if (*at >= len) return -1;
    uint8_t first = p[(*at)++];
    if (first < 0x80) return first;
    int bytes = first & 0x7f;
    if (bytes > 4 || *at + (size_t)bytes > len) return -1;
    long value = 0;
    while (bytes--) value = (value << 8) | p[(*at)++];
    return value;
}

// An LDAP message's operation tag, after its envelope (SEQUENCE, message id).
static int ldap_tag(const uint8_t *p, size_t len, size_t *at)
{
    *at = 1;
    if (len < 2 || p[0] != 0x30 || ber_length(p, len, at) < 0 || *at >= len || p[*at] != 0x02) return -1;
    (*at)++;
    long id = ber_length(p, len, at);
    if (id < 0 || (*at += (size_t)id) >= len) return -1;
    return p[*at];
}

static const char *const smtp_commands[] = {"HELO", "EHLO", "MAIL", "RCPT", "DATA", "RSET", "VRFY", "NOOP", "QUIT", "AUTH", "STARTTLS", "BDAT", 0};
static const char *const ftp_commands[] = {"USER", "PASS", "ACCT", "CWD", "CDUP", "QUIT", "PASV", "EPSV", "PORT", "EPRT", "TYPE", "MODE", "RETR",
    "STOR", "STOU", "APPE", "REST", "RNFR", "RNTO", "ABOR", "DELE", "RMD", "MKD", "PWD", "LIST", "NLST", "SITE", "SYST", "STAT", "NOOP",
    "FEAT", "OPTS", "AUTH", "SIZE", "MDTM", "MLSD", "MLST", 0};
static const char *const pop3_commands[] = {"USER", "PASS", "APOP", "STAT", "LIST", "RETR", "DELE", "NOOP", "RSET", "QUIT", "TOP", "UIDL", "CAPA",
    "STLS", "AUTH", 0};
static const char *const imap_commands[] = {"CAPABILITY", "NOOP", "LOGOUT", "STARTTLS", "AUTHENTICATE", "LOGIN", "SELECT", "EXAMINE", "CREATE",
    "DELETE", "RENAME", "SUBSCRIBE", "UNSUBSCRIBE", "LIST", "LSUB", "STATUS", "APPEND", "CHECK", "CLOSE", "EXPUNGE", "SEARCH", "FETCH",
    "STORE", "COPY", "UID", "IDLE", "MOVE", "NAMESPACE", "ENABLE", 0};
static const char *const memcached_commands[] = {"GET", "GETS", "GAT", "GATS", "SET", "ADD", "REPLACE", "APPEND", "PREPEND", "CAS", "DELETE",
    "INCR", "DECR", "TOUCH", "STATS", "FLUSH_ALL", "VERSION", "VERBOSITY", 0};
static const char *const kafka_apis[] = {"Produce", "Fetch", "ListOffsets", "Metadata", "LeaderAndIsr", "StopReplica", "UpdateMetadata",
    "ControlledShutdown", "OffsetCommit", "OffsetFetch", "FindCoordinator", "JoinGroup", "Heartbeat", "LeaveGroup", "SyncGroup",
    "DescribeGroups", "ListGroups", "SaslHandshake", "ApiVersions", "CreateTopics", "DeleteTopics", "DeleteRecords", "InitProducerId"};
static const struct { uint16_t class_id, method_id; const char *name; uint8_t replied; } amqp_methods[] = {
    {20, 10, "channel.open", 1}, {20, 40, "channel.close", 1}, {40, 10, "exchange.declare", 1}, {40, 20, "exchange.delete", 1},
    {40, 30, "exchange.bind", 1}, {50, 10, "queue.declare", 1}, {50, 20, "queue.bind", 1}, {50, 30, "queue.purge", 1},
    {50, 40, "queue.delete", 1}, {50, 50, "queue.unbind", 1}, {60, 10, "basic.qos", 1}, {60, 20, "basic.consume", 1},
    {60, 30, "basic.cancel", 1}, {60, 40, "basic.publish", 0}, {60, 70, "basic.get", 1}, {60, 80, "basic.ack", 0},
    {60, 90, "basic.reject", 0}, {60, 110, "basic.recover", 1}, {60, 120, "basic.nack", 0}, {85, 10, "confirm.select", 1},
    {90, 10, "tx.select", 1}, {90, 20, "tx.commit", 1}, {90, 30, "tx.rollback", 1},
};

// Each protocol besides HTTP: how to recognize it, by a server's greeting (the first thing said on a connection
// seen opening) or by a message only its clients send, whole in the packet where it has a length; and how to
// read its requests and replies once recognized. n is what was captured, len the payload's real length.

// Whether text starts with a word (a query's first keyword) or a comment.
static int starts_text(const uint8_t *p, size_t n)
{
    size_t at = 0, letters = 0;
    while (at < n && (p[at] == ' ' || p[at] == '\n' || p[at] == '\r' || p[at] == '\t' || p[at] == '(')) at++;
    while (at + letters < n && (p[at + letters] | 0x20) >= 'a' && (p[at + letters] | 0x20) <= 'z') letters++;
    return letters >= 2 || (at + 1 < n && ((p[at] == '-' && p[at + 1] == '-') || (p[at] == '/' && p[at + 1] == '*')));
}

// Digits then CRLF at *at, moving past them.
static int digits_crlf(const uint8_t *p, size_t n, size_t *at)
{
    size_t start = *at;
    while (*at < n && p[*at] >= '0' && p[*at] <= '9') (*at)++;
    if (*at == start || *at + 2 > n || p[*at] != '\r' || p[*at + 1] != '\n') return 0;
    *at += 2;
    return 1;
}

// A packet of whole lines whose first is one of the known commands, then a space or the line's end.
static int command_line(const uint8_t *p, size_t n, size_t len, const char *const *known)
{
    size_t eol = 0;
    while (eol + 1 < n && !(p[eol] == '\r' && p[eol + 1] == '\n')) eol++;
    if (eol + 1 >= n || (len <= n && (p[len - 2] != '\r' || p[len - 1] != '\n'))) return 0;
    size_t w = 0;
    while (w < eol && w < 15 && (((p[w] | 0x20) >= 'a' && (p[w] | 0x20) <= 'z') || p[w] == '_')) w++;
    if (!w || (p[w] != ' ' && p[w] != '\r')) return 0;
    char word[16];
    for (size_t i = 0; i < w; i++) word[i] = (char)(p[i] >= 'a' ? p[i] - 32 : p[i]);
    word[w] = '\0';
    for (; *known; known++) if (strcmp(word, *known) == 0) return 1;
    return 0;
}

// SMTP's and FTP's greeting alike: the client's first command tells them apart.
static int ready_220(const uint8_t *p, size_t n, size_t len)
{
    (void)len;
    return n >= 4 && reply_code(p, n) == 220 && (p[3] == ' ' || p[3] == '-');
}

// A reply with a three-digit code: 4xx and 5xx are errors.
static int coded_reply(struct flow *f, const uint8_t *p, size_t n)
{
    (void)f;
    int code = reply_code(p, n);
    return code < 0 ? -1 : code >= 400;
}

// PostgreSQL. Startup, SSL or GSS encryption request, cancel; or messages of the query protocols back to back to
// the end of the packet, starting with one only clients send (Query, Parse, Bind).
static int postgres_detect(const uint8_t *p, size_t n, size_t len)
{
    if (n >= 8 && be32(p) == len)
    {
        uint32_t code = be32(p + 4);
        if (code == 196608 || code == 80877103 || code == 80877104 || code == 80877102) return 1;
    }
    if (n < 6 || (p[0] != 'Q' && p[0] != 'P' && p[0] != 'B') || (p[0] == 'Q' && !starts_text(p + 5, n - 5))) return 0;
    size_t at = 0;
    int messages = 0;
    for (; at + 5 <= n; messages++)
    {
        uint32_t size = be32(p + at + 1);
        if (!memchr("QPBDESCH", p[at], 8) || size < 4 || at + 1 + size > len) return 0;
        at += 1 + size;
    }
    return at == len || messages >= 2;
}

static int postgres_request(struct flow *f, const uint8_t *p, size_t n, char *op, size_t op_len)
{
    (void)f;
    if (n >= 5 && (p[0] == 'Q' || p[0] == 'P'))
    {
        size_t at = 5;
        if (p[0] == 'P') { while (at < n && p[at]) at++; at++; } // the statement's name
        if (at < n) { first_word(p + at, n - at, op, op_len); return 1; }
    }
    if (n >= 5 && (p[0] == 'B' || p[0] == 'E')) { snprintf(op, op_len, "EXECUTE"); return 1; }
    return 0;
}

static int postgres_reply(struct flow *f, const uint8_t *p, size_t n)
{
    (void)f;
    for (size_t at = 0; at + 5 <= n; at += 1 + be32(p + at + 1))
    {
        if (p[at] == 'E') return 1;
        if (be32(p + at + 1) < 4) break;
    }
    return 0;
}

// MySQL. The server's handshake: protocol 10, then its version, NUL-terminated.
static int mysql_greets(const uint8_t *p, size_t n, size_t len)
{
    if (n < 7 || p[3] != 0 || p[4] != 0x0a || le24(p) + 4 != len) return 0;
    size_t at = 5;
    while (at < n && p[at] >= 0x20 && p[at] < 0x7f) at++;
    return at > 5 && at < n && p[at] == 0;
}

// A query, a statement to prepare or a ping, as the first packet of a command.
static int mysql_detect(const uint8_t *p, size_t n, size_t len)
{
    if (n < 5 || p[3] != 0 || le24(p) + 4 != len) return 0;
    if (p[4] == 0x03) { size_t at = n >= 7 && p[5] == 0 && p[6] == 1 ? 7 : 5; return starts_text(p + at, n - at); }
    if (p[4] == 0x16) return starts_text(p + 5, n - 5);
    return p[4] == 0x0e && len == 5;
}

static int mysql_request(struct flow *f, const uint8_t *p, size_t n, char *op, size_t op_len)
{
    (void)f;
    if (n < 5 || p[3] != 0) return 0;
    // COM_QUERY; with query attributes (MySQL 8.0.23 and later) the text follows their counts, 0 and 1.
    if (p[4] == 0x03) { size_t at = n >= 7 && p[5] == 0 && p[6] == 1 ? 7 : 5; first_word(p + at, n - at, op, op_len); return 1; }
    snprintf(op, op_len, "%s", p[4] == 0x16 ? "PREPARE" : p[4] == 0x17 ? "EXECUTE" : p[4] == 0x0e ? "PING" : "");
    return op[0] ? 1 : 0;
}

static int mysql_reply(struct flow *f, const uint8_t *p, size_t n) { (void)f; return n >= 5 && p[4] == 0xff; }

// Redis. An array of bulk strings, the first a command: "*2\r\n$3\r\nGET\r\n...".
static int redis_detect(const uint8_t *p, size_t n, size_t len)
{
    (void)len;
    size_t at = 1;
    if (n < 2 || p[0] != '*' || !digits_crlf(p, n, &at) || at >= n || p[at++] != '$' || !digits_crlf(p, n, &at)) return 0;
    return starts_text(p + at, n - at);
}

static int redis_request(struct flow *f, const uint8_t *p, size_t n, char *op, size_t op_len)
{
    (void)f;
    if (n < 4) return 0;
    if (p[0] == '*')
    {
        size_t at = 1;
        while (at < n && p[at] != '$') at++;
        while (at < n && p[at] != '\n') at++;
        if (at + 1 >= n) return 0;
        first_word(p + at + 1, n - at - 1, op, op_len);
        return 1;
    }
    first_word(p, n, op, op_len);
    return 1;
}

static int redis_reply(struct flow *f, const uint8_t *p, size_t n) { (void)f; return n >= 1 && p[0] == '-'; }

// MongoDB. A message answering nothing (responseTo 0): OP_MSG, or a legacy OP_QUERY on a named collection.
static int mongodb_detect(const uint8_t *p, size_t n, size_t len)
{
    if (n < 21 || le32(p) != len || le32(p + 8) != 0) return 0;
    uint32_t op = le32(p + 12);
    return (op == 2013 && p[20] <= 1) || (op == 2004 && starts_text(p + 20, n - 20));
}

static int mongodb_request(struct flow *f, const uint8_t *p, size_t n, char *op, size_t op_len)
{
    (void)f;
    if (n < 27 || le32(p + 8) != 0) return 0;
    if (le32(p + 12) == 2013 && p[20] == 0) { token(p + 26, n - 26, "", op, op_len); return op[0] ? 1 : 0; }
    if (le32(p + 12) == 2004) { snprintf(op, op_len, "query"); return 1; }
    return 0;
}

// LDAP. A request's operation, by its tag.
static const char *ldap_operation(int tag)
{
    return tag == 0x60 ? "bind" : tag == 0x63 ? "search" : tag == 0x66 ? "modify" : tag == 0x68 ? "add"
        : tag == 0x4a ? "delete" : tag == 0x6c ? "modifyDN" : tag == 0x6e ? "compare" : tag == 0x77 ? "extended"
        : tag == 0x42 ? "unbind" : tag == 0x50 ? "abandon" : NULL;
}

static int ldap_detect(const uint8_t *p, size_t n, size_t len)
{
    size_t at = 1, op;
    long size = ber_length(p, n, &at);
    return size >= 0 && at + (size_t)size == len && ldap_operation(ldap_tag(p, n, &op)) != NULL;
}

static int ldap_request(struct flow *f, const uint8_t *p, size_t n, char *op, size_t op_len)
{
    (void)f;
    size_t at;
    int tag = ldap_tag(p, n, &at);
    const char *name = ldap_operation(tag);
    if (!name) return 0;
    snprintf(op, op_len, "%s", name);
    return tag == 0x42 || tag == 0x50 ? 2 : 1;
}

static int ldap_reply(struct flow *f, const uint8_t *p, size_t n)
{
    (void)f;
    size_t at;
    int tag = ldap_tag(p, n, &at);
    if (tag < 0) return -1;
    if (tag == 0x64 || tag == 0x73 || tag == 0x79) return 0; // a search entry, reference or intermediate: results follow
    at++;
    if (ber_length(p, n, &at) < 0 || at + 2 >= n || p[at] != 0x0a) return 0;
    int code = p[at + 2];
    return code != 0 && !(tag == 0x6f && (code == 5 || code == 6)); // compare: false and true are answers
}

// SQL Server. A TDS packet a client sends (pre-login, login, SQL batch, RPC, transaction), ending its message.
static int sqlserver_detect(const uint8_t *p, size_t n, size_t len)
{
    return n >= 9 && be16(p + 2) == len && (p[1] & 1) && (p[1] & ~0x1b) == 0 && p[7] == 0
        && (p[0] == 0x12 || p[0] == 0x10 || p[0] == 0x01 || p[0] == 0x03 || p[0] == 0x0e);
}

static int sqlserver_request(struct flow *f, const uint8_t *p, size_t n, char *op, size_t op_len)
{
    (void)f;
    // A SQL batch's text is UTF-16 after its headers; remote procedure calls (prepared statements) as RPC.
    if (n >= 12 && p[0] == 0x01)
    {
        size_t at = 8 + le32(p + 8);
        uint8_t text[48];
        size_t k = 0;
        for (; at + 1 < n && k < sizeof text; at += 2) text[k++] = p[at + 1] ? '?' : p[at];
        first_word(text, k, op, op_len);
        return 1;
    }
    snprintf(op, op_len, "%s", n >= 8 ? (p[0] == 0x03 ? "RPC" : p[0] == 0x0e ? "TRANSACTION" : p[0] == 0x10 ? "LOGIN" : "") : "");
    return op[0] ? 1 : 0;
}

static int sqlserver_reply(struct flow *f, const uint8_t *p, size_t n) { (void)f; return n < 9 || p[0] != 0x04 ? -1 : p[8] == 0xaa; }

// Cassandra. A request frame (v3 or v4) with a request's opcode.
static int cassandra_detect(const uint8_t *p, size_t n, size_t len)
{
    return n >= 9 && (p[0] == 3 || p[0] == 4) && (p[1] & 0xe0) == 0 && 9 + (size_t)be32(p + 5) == len
        && (p[4] == 0x01 || p[4] == 0x05 || p[4] == 0x07 || p[4] == 0x09 || p[4] == 0x0a || p[4] == 0x0b || p[4] == 0x0d || p[4] == 0x0f);
}

static int cassandra_request(struct flow *f, const uint8_t *p, size_t n, char *op, size_t op_len)
{
    (void)f;
    if (n < 9 || (p[0] & 0x80) || p[0] < 3 || p[0] > 4) return 0;
    uint8_t code = p[4];
    if (code == 0x07 && n >= 13) { first_word(p + 13, n - 13, op, op_len); return 1; }
    const char *name = code == 0x09 ? "PREPARE" : code == 0x0a ? "EXECUTE" : code == 0x0d ? "BATCH" : code == 0x01 ? "STARTUP"
        : code == 0x05 ? "OPTIONS" : code == 0x0b ? "REGISTER" : code == 0x0f ? "AUTH" : NULL;
    if (!name) return 0;
    snprintf(op, op_len, "%s", name);
    return 1;
}

static int cassandra_reply(struct flow *f, const uint8_t *p, size_t n) { (void)f; return n < 5 || !(p[0] & 0x80) ? -1 : p[4] == 0x00; }

// Kafka. A request header: API key and version, correlation id, then the client's id as text.
static int kafka_detect(const uint8_t *p, size_t n, size_t len)
{
    if (n < 15 || 4 + (size_t)be32(p) != len || be16(p + 4) >= 100 || be16(p + 6) > 30) return 0;
    size_t id = be16(p + 12);
    if (id == 0 || id > 255 || 14 + id > len) return 0;
    for (size_t i = 14; i < 14 + id && i < n; i++) if (p[i] < 0x20 || p[i] >= 0x7f) return 0;
    return 1;
}

static int kafka_request(struct flow *f, const uint8_t *p, size_t n, char *op, size_t op_len)
{
    (void)f;
    if (n < 12 || be32(p) > 100 * 1024 * 1024) return 0;
    unsigned api = be16(p + 4);
    if (api >= 100 || be16(p + 6) > 30) return 0;
    if (api < sizeof kafka_apis / sizeof kafka_apis[0]) snprintf(op, op_len, "%s", kafka_apis[api]);
    else snprintf(op, op_len, "API %u", api);
    return 1;
}

// MQTT. CONNECT, naming the protocol, or a ping.
static int mqtt_detect(const uint8_t *p, size_t n, size_t len)
{
    if (len == 2) return n == 2 && p[0] == 0xc0 && p[1] == 0;
    size_t at = 1;
    while (at < n && at < 4 && (p[at] & 0x80)) at++;
    at++;
    return n >= 2 && p[0] == 0x10 && ((at + 6 <= n && memcmp(p + at, "\0\4MQTT", 6) == 0) || (at + 8 <= n && memcmp(p + at, "\0\6MQIsdp", 8) == 0));
}

static int mqtt_request(struct flow *f, const uint8_t *p, size_t n, char *op, size_t op_len)
{
    (void)f;
    if (n < 2) return 0;
    unsigned type = p[0] >> 4;
    const char *name = type == 1 ? "CONNECT" : type == 3 ? "PUBLISH" : type == 8 ? "SUBSCRIBE" : type == 10 ? "UNSUBSCRIBE"
        : type == 12 ? "PINGREQ" : type == 14 ? "DISCONNECT" : NULL;
    if (!name) return 0;
    snprintf(op, op_len, "%s", name);
    return type == 14 || (type == 3 && ((p[0] >> 1) & 3) == 0) ? 2 : 1;
}

static int mqtt_reply(struct flow *f, const uint8_t *p, size_t n)
{
    (void)f;
    unsigned type = n ? p[0] >> 4 : 0;
    if (type == 2) return n >= 4 && p[3] != 0;
    if (type == 9) return p[n - 1] == 0x80;
    return type == 4 || type == 5 || type == 11 || type == 13 ? 0 : -1;
}

// Memcached. A binary request, its header's body length the rest of the packet; or a text command, in the lower
// case its clients write (its server answers in capitals: "VERSION 1.6").
static int memcached_detect(const uint8_t *p, size_t n, size_t len)
{
    if (n >= 24 && p[0] == 0x80) return p[5] == 0 && 24 + (size_t)be32(p + 8) == len && 24 + (size_t)p[4] + be16(p + 2) <= len;
    return n && p[0] >= 'a' && command_line(p, n, len, memcached_commands);
}

static int memcached_request(struct flow *f, const uint8_t *p, size_t n, char *op, size_t op_len)
{
    (void)f;
    if (n >= 24 && p[0] == 0x80)
    {
        const char *names[] = {"GET", "SET", "ADD", "REPLACE", "DELETE", "INCREMENT", "DECREMENT", "QUIT", "FLUSH", "GETQ", "NOOP", "VERSION", "GETK"};
        if (p[1] < sizeof names / sizeof names[0]) snprintf(op, op_len, "%s", names[p[1]]);
        else snprintf(op, op_len, "OP %u", p[1]);
        return 1;
    }
    return command(p, n, memcached_commands, op, op_len);
}

static int memcached_reply(struct flow *f, const uint8_t *p, size_t n)
{
    (void)f;
    if (n >= 24 && p[0] == 0x81) return be16(p + 6) != 0;
    return (n >= 5 && memcmp(p, "ERROR", 5) == 0) || (n >= 12 && memcmp(p, "CLIENT_ERROR", 12) == 0) || (n >= 12 && memcmp(p, "SERVER_ERROR", 12) == 0);
}

// AMQP. The 0-9-1 protocol header, or a method frame only clients send.
static int amqp_detect(const uint8_t *p, size_t n, size_t len)
{
    if (len == 8) return n == 8 && memcmp(p, "AMQP\0\0\x09\x01", 8) == 0;
    if (n < 11 || p[0] != 1 || 8 + (size_t)be32(p + 3) > len) return 0;
    size_t end = 7 + (size_t)be32(p + 3);
    if (end < n && p[end] != 0xce) return 0;
    unsigned class_id = be16(p + 7), method_id = be16(p + 9);
    if ((class_id == 20 && method_id == 40) || (class_id == 60 && (method_id == 80 || method_id == 120))) return 0; // either side sends these
    for (size_t i = 0; i < sizeof amqp_methods / sizeof amqp_methods[0]; i++)
    {
        if (amqp_methods[i].class_id == class_id && amqp_methods[i].method_id == method_id) return 1;
    }
    return 0;
}

static int amqp_request(struct flow *f, const uint8_t *p, size_t n, char *op, size_t op_len)
{
    (void)f;
    if (n < 11 || p[0] != 1) return 0;
    for (size_t i = 0; i < sizeof amqp_methods / sizeof amqp_methods[0]; i++)
    {
        if (amqp_methods[i].class_id == be16(p + 7) && amqp_methods[i].method_id == be16(p + 9))
        {
            snprintf(op, op_len, "%s", amqp_methods[i].name);
            return amqp_methods[i].replied ? 1 : 2;
        }
    }
    return 0;
}

static int amqp_reply(struct flow *f, const uint8_t *p, size_t n)
{
    (void)f;
    if (n < 11 || p[0] != 1) return -1;
    return (be16(p + 7) == 20 && be16(p + 9) == 40) || (be16(p + 7) == 10 && be16(p + 9) == 50); // channel.close, connection.close
}

// SMTP, FTP, POP3: command lines.
static int smtp_detect(const uint8_t *p, size_t n, size_t len) { return command_line(p, n, len, smtp_commands); }
static int smtp_request(struct flow *f, const uint8_t *p, size_t n, char *op, size_t op_len) { (void)f; return command(p, n, smtp_commands, op, op_len); }
static int ftp_detect(const uint8_t *p, size_t n, size_t len) { return command_line(p, n, len, ftp_commands); }
static int ftp_request(struct flow *f, const uint8_t *p, size_t n, char *op, size_t op_len) { (void)f; return command(p, n, ftp_commands, op, op_len); }

static int pop3_greets(const uint8_t *p, size_t n, size_t len) { (void)len; return n >= 4 && memcmp(p, "+OK", 3) == 0 && (p[3] == ' ' || p[3] == '\r'); }
static int pop3_detect(const uint8_t *p, size_t n, size_t len) { return command_line(p, n, len, pop3_commands); }
static int pop3_request(struct flow *f, const uint8_t *p, size_t n, char *op, size_t op_len) { (void)f; return command(p, n, pop3_commands, op, op_len); }
static int pop3_reply(struct flow *f, const uint8_t *p, size_t n)
{
    (void)f;
    return n >= 4 && memcmp(p, "-ERR", 4) == 0 ? 1 : n >= 3 && memcmp(p, "+OK", 3) == 0 ? 0 : -1;
}

// IMAP. "<tag> <command> ...", the tag coming back on the command's completion; recognized by a tag with a digit
// in it ("a1", "A0001").
static int imap_greets(const uint8_t *p, size_t n, size_t len)
{
    (void)len;
    return (n >= 5 && memcmp(p, "* OK ", 5) == 0) || (n >= 10 && memcmp(p, "* PREAUTH ", 10) == 0);
}

static int imap_detect(const uint8_t *p, size_t n, size_t len)
{
    size_t t = 0;
    int digit = 0;
    while (t < n && (((p[t] | 0x20) >= 'a' && (p[t] | 0x20) <= 'z') || (p[t] >= '0' && p[t] <= '9') || p[t] == '.'))
    {
        digit |= p[t] >= '0' && p[t] <= '9';
        t++;
    }
    return digit && t + 1 < n && p[t] == ' ' && command_line(p + t + 1, n - t - 1, len - t - 1, imap_commands);
}

static int imap_request(struct flow *f, const uint8_t *p, size_t n, char *op, size_t op_len)
{
    char tag[sizeof f->tag];
    token(p, n, "", tag, sizeof tag);
    size_t at = strlen(tag) + 1;
    if (!tag[0] || at >= n || !command(p + at, n - at, imap_commands, op, op_len)) return 0;
    memcpy(f->tag, tag, sizeof f->tag);
    return 1;
}

static int imap_reply(struct flow *f, const uint8_t *p, size_t n)
{
    size_t length = strlen(f->tag);
    for (size_t at = 0; length && at + length + 3 <= n; at++)
    {
        if ((at == 0 || p[at - 1] == '\n') && memcmp(p + at, f->tag, length) == 0 && p[at + length] == ' ')
            return memcmp(p + at + length + 1, "NO", 2) == 0 || memcmp(p + at + length + 1, "BA", 2) == 0;
    }
    return 0;
}

static const struct protocol protocols[] = {
    {"postgres", NULL, postgres_detect, postgres_request, postgres_reply},
    {"mysql", mysql_greets, mysql_detect, mysql_request, mysql_reply},
    {"redis", NULL, redis_detect, redis_request, redis_reply},
    {"mongodb", NULL, mongodb_detect, mongodb_request, NULL},
    {"ldap", NULL, ldap_detect, ldap_request, ldap_reply},
    {"sqlserver", NULL, sqlserver_detect, sqlserver_request, sqlserver_reply},
    {"cassandra", NULL, cassandra_detect, cassandra_request, cassandra_reply},
    {"kafka", NULL, kafka_detect, kafka_request, NULL},
    {"mqtt", NULL, mqtt_detect, mqtt_request, mqtt_reply},
    {"memcached", NULL, memcached_detect, memcached_request, memcached_reply},
    {"amqp", NULL, amqp_detect, amqp_request, amqp_reply},
    {"smtp", ready_220, smtp_detect, smtp_request, coded_reply},
    {"ftp", ready_220, ftp_detect, ftp_request, coded_reply},
    {"pop3", pop3_greets, pop3_detect, pop3_request, pop3_reply},
    {"imap", imap_greets, imap_detect, imap_request, imap_reply},
};
#define PROTOCOLS (sizeof protocols / sizeof protocols[0])
_Static_assert(PROTOCOLS <= 32, "candidates is a 32-bit mask");

// A flow's protocol: the one whose greeting or client message this packet is. When several fit (a "220" greeting,
// a command word two protocols share) the flow waits for a packet that tells them apart; when none does, it has
// no protocol. The client is whoever opened the connection, when that was seen; otherwise the side whose message
// it was.
static void identify(struct flow *f, int outgoing, const uint8_t *p, size_t available, size_t len)
{
    size_t n = available < len ? available : len;   // short frames come padded
    int greeting = f->opened && !f->spoke && outgoing != f->opener_outgoing, from_client = !greeting;
    uint32_t matches = 0;
    const struct protocol *found = NULL;
    f->spoke = 1;
    for (size_t i = 0; i < PROTOCOLS; i++)
    {
        int (*test)(const uint8_t *, size_t, size_t) = greeting ? protocols[i].greets : protocols[i].detect;
        if ((!f->candidates || (f->candidates >> i & 1)) && test && test(p, n, len)) { matches |= 1u << i; found = &protocols[i]; }
    }
    if (!matches) return;
    if (matches & (matches - 1)) { if (greeting) f->candidates = matches; return; }

    if (f->opened) from_client = outgoing == f->opener_outgoing;
    f->protocol = found;
    f->server_port = outgoing == from_client ? f->remote_port : f->local_port;
}

// The protocols besides HTTP, once identified; server_port tells requests from replies. Only the first request
// of a packet is read, and the first reply packet answers it.
static void track_request(struct flow *f, int outgoing, const uint8_t *p, size_t available, size_t payload_len)
{
    if ((outgoing ? f->remote_port : f->local_port) == f->server_port)
    {
        char op[48] = "";
        int kind = f->protocol->request(f, p, available, op, sizeof op);
        if (kind == 1) request(f, outgoing, op, payload_len);
        else if (kind == 2) fire(f, outgoing, op, payload_len);
        else if (f->pending) { struct exchange *e = exchange_of(f); if (e) e->request_bytes += payload_len; }
        return;
    }

    struct exchange *e = f->operation[0] ? exchange_of(f) : NULL;
    if (e) e->response_bytes += payload_len;
    if (!f->pending) return;
    int reply = f->protocol->reply ? f->protocol->reply(f, p, available) : 0;
    if (reply >= 0) response(f, reply);
}

static void read_http_response(struct flow *f, int outgoing, const uint8_t *p, size_t len)
{
    // "HTTP/1.1 200 OK"
    if (len < 12 || memcmp(p, "HTTP/", 5) != 0) return;
    size_t at = 5;
    while (at < len && p[at] != ' ') at++;
    if (at + 4 > len || p[at + 1] < '1' || p[at + 1] > '5' || p[at + 2] < '0' || p[at + 2] > '9' || p[at + 3] < '0' || p[at + 3] > '9') return;
    f->http_status = (uint16_t)((p[at + 1] - '0') * 100 + (p[at + 2] - '0') * 10 + (p[at + 3] - '0'));
    if (f->request_us && outgoing != f->request_outgoing)
    {
        f->response_us = (uint32_t)(now_us() - f->request_us);
        f->request_us = 0;
        response(f, f->http_status >= 500);
    }
}

static void read_http_request(struct flow *f, int outgoing, const uint8_t *p, size_t len)
{
    static const char *methods[] = {"GET ", "POST ", "PUT ", "HEAD ", "DELETE ", "PATCH ", "OPTIONS "};
    for (size_t m = 0; m < sizeof methods / sizeof methods[0]; m++)
    {
        size_t method_len = strlen(methods[m]);
        if (len <= method_len || memcmp(p, methods[m], method_len) != 0) continue;

        token(p, method_len - 1, "", f->http_method, sizeof f->http_method);
        f->request_us = now_us();
        f->request_outgoing = (uint8_t)outgoing;
        f->http_status = 0;
        f->response_us = 0;
        token(p + method_len, len - method_len, "?#", f->http_path, sizeof f->http_path);
        char operation[48];
        snprintf(operation, sizeof operation, "%s %s", f->http_method, f->http_path[0] ? f->http_path : "/");
        f->protocol = &http;
        request(f, outgoing, operation, len);
        f->http_host[0] = '\0';
        for (size_t i = method_len; i + 6 < len; i++)
        {
            if (p[i] == '\n' && strncasecmp((const char *)p + i + 1, "host:", 5) == 0)
            {
                size_t at = i + 6;
                while (at < len && p[at] == ' ') at++;
                token(p + at, len - at, "", f->http_host, sizeof f->http_host);
                break;
            }
        }
        return;
    }
}

static void track_tcp(struct flow *f, int outgoing, const uint8_t *tcp, size_t captured, size_t payload_len)
{
    uint8_t flags = tcp[13];
    int syn = flags & 0x02, ack = flags & 0x10, fin = flags & 0x01, rst = flags & 0x04;

    // A connection starting (perhaps again, on the same ports): what it speaks is read anew.
    if (syn && !ack)
    {
        f->opened = 1;
        f->opener_outgoing = (uint8_t)outgoing;
        f->spoke = f->pending = 0;
        f->candidates = 0;
        f->protocol = NULL;
    }

    // Connecting: our SYN, then their SYN-ACK. Accepting: our SYN-ACK, then their ACK.
    if (syn && outgoing) { f->handshake_us = now_us(); f->connecting = !ack; }
    else if (f->handshake_us && !outgoing && ((f->connecting && syn && ack) || (!f->connecting && !syn && ack)))
    {
        f->rtt_us = (uint32_t)(now_us() - f->handshake_us);
        f->handshake_us = 0;
        f->established = 1;
        f->unanswered = 0;
    }

    if (rst)
    {
        if (outgoing) f->resets_out++; else f->resets_in++;
        if (!outgoing && f->connecting && f->handshake_us && !f->established) { f->refused = 1; f->handshake_us = 0; }
    }
    else if (!syn && be16(tcp + 14) == 0)
    {
        if (outgoing) f->zero_window_out++; else f->zero_window_in++;
    }

    // A segment ending at or before the highest sequence number already seen that way carries data sent
    // before. One-byte segments are left out: keep-alive and window probes resend a byte on purpose.
    uint32_t length = (uint32_t)payload_len + (syn ? 1 : 0) + (fin ? 1 : 0);
    if (length > 1 || syn)
    {
        uint32_t end = be32(tcp + 4) + length;
        uint32_t *next = outgoing ? &f->next_out : &f->next_in;
        uint8_t *set = outgoing ? &f->seq_out_set : &f->seq_in_set;
        if (*set && (int32_t)(end - *next) <= 0) { if (outgoing) f->retrans_out++; else f->retrans_in++; }
        else { *next = end; *set = 1; }
    }

    size_t header = (tcp[12] >> 4) * 4;
    if (payload_len && captured > header)
    {
        const uint8_t *payload = tcp + header;
        size_t available = captured - header;
        if (!f->protocol) identify(f, outgoing, payload, available, payload_len);
        if (f->protocol && f->protocol != &http) track_request(f, outgoing, payload, available, payload_len);
        else if (f->protocol == &http && f->pending && outgoing != f->to_server_outgoing && !(available >= 5 && memcmp(payload, "HTTP/", 5) == 0))
        {
            struct exchange *e = exchange_of(f);
            if (e) e->response_bytes += payload_len;
        }

        if (payload[0] == 0x16)
        {
            if (available > 5 && payload[5] == 0x01 && !f->client_hello_seen) read_client_hello(f, payload, available);
            else if (available > 5 && payload[5] == 0x02 && !f->tls_version[0]) read_server_hello(f, payload, available);
        }
        else if (available >= 5 && memcmp(payload, "HTTP/", 5) == 0)
        {
            struct exchange *e = f->protocol == &http && f->pending ? exchange_of(f) : NULL;
            if (e) e->response_bytes += payload_len;
            read_http_response(f, outgoing, payload, available);
        }
        else if (!f->protocol || f->protocol == &http) read_http_request(f, outgoing, payload, available);
    }
}

// ICMP that reports on a packet this container sent: its headers come back inside, so the flow can be found.
static void track_icmp(int family, const uint8_t *icmp, size_t len)
{
    int unreachable = family == 4 ? icmp[0] == 3 : icmp[0] == 1;
    int too_big = family == 4 ? icmp[0] == 3 && icmp[1] == 4 : icmp[0] == 2;
    if (!unreachable && !too_big) return;
    const uint8_t *inner = icmp + 8;
    size_t inner_len = len - 8;
    struct flow key = {0};
    const uint8_t *transport;
    if (family == 4 && inner_len >= 28)
    {
        key.family = 4;
        key.proto = inner[9];
        memcpy(key.local + 12, inner + 12, 4);
        memcpy(key.remote + 12, inner + 16, 4);
        transport = inner + (inner[0] & 0x0f) * 4;
        if (transport + 4 > icmp + len) return;
    }
    else if (family == 6 && inner_len >= 44)
    {
        key.family = 6;
        key.proto = inner[6];
        memcpy(key.local, inner + 8, 16);
        memcpy(key.remote, inner + 24, 16);
        transport = inner + 40;
    }
    else return;
    if (key.proto != IPPROTO_TCP && key.proto != IPPROTO_UDP) return;
    key.local_port = (uint16_t)be16(transport);
    key.remote_port = (uint16_t)be16(transport + 2);
    struct flow *f = existing(&key);
    if (!f) return;
    if (too_big) f->too_big++; else f->unreachable++;
    f->active = 1;
}

static void capture(const uint8_t *packet, size_t captured, size_t full)
{
    struct timespec t;
    clock_gettime(CLOCK_REALTIME, &t);
    size_t n = captured < SNAP ? captured : SNAP;
    static const char digits[] = "0123456789abcdef";
    char hex[SNAP * 2 + 1];
    for (size_t i = 0; i < n; i++)
    {
        hex[i * 2] = digits[packet[i] >> 4];
        hex[i * 2 + 1] = digits[packet[i] & 0x0f];
    }
    hex[n * 2] = '\0';
    printf("P %lld %ld %zu %s\n", (long long)t.tv_sec, t.tv_nsec / 1000, full, hex);
    captured_packets++;
}

static void read_command(void)
{
    char line[64];
    if (!fgets(line, sizeof line, stdin)) { capture_until_us = 0; return; }
    int seconds;
    if (sscanf(line, "C %d", &seconds) == 1 && seconds > 0 && seconds <= 60)
    {
        capture_until_us = now_us() + (uint64_t)seconds * 1000000;
        captured_packets = 0;
    }
}

static int read_name(const uint8_t *msg, size_t len, size_t *offset, char *out, size_t out_len)
{
    size_t pos = *offset, written = 0;
    int jumped = 0, hops = 0;
    while (pos < len)
    {
        uint8_t label = msg[pos];
        if (label == 0)
        {
            if (!jumped) *offset = pos + 1;
            out[written ? written - 1 : 0] = '\0';
            return written > 0;
        }

        if ((label & 0xc0) == 0xc0)
        {
            if (pos + 1 >= len || ++hops > 16) return 0;
            if (!jumped) *offset = pos + 2;
            pos = ((label & 0x3f) << 8) | msg[pos + 1];
            jumped = 1;
            continue;
        }

        if (pos + 1 + label > len || written + label + 1 >= out_len) return 0;
        for (int i = 0; i < label; i++)
        {
            char c = (char)msg[pos + 1 + i];
            out[written++] = (c > 32 && c < 127) ? c : '?';
        }

        out[written++] = '.';
        pos += 1 + label;
    }

    return 0;
}

static void parse_dns(const uint8_t *msg, size_t len)
{
    static const char *rcodes[] = {"NOERROR", "FORMERR", "SERVFAIL", "NXDOMAIN", "NOTIMP", "REFUSED"};
    if (len < 12) return;
    int reply = msg[2] & 0x80;
    if (!reply)
    {
        size_t at = 12;
        char asked[256];
        if (((msg[4] << 8) | msg[5]) < 1 || !read_name(msg, len, &at, asked, sizeof asked)) return;
        pending[pending_next].id = (uint16_t)((msg[0] << 8) | msg[1]);
        pending[pending_next].name_hash = hash_name(asked);
        pending[pending_next].sent_us = now_us();
        pending_next = (pending_next + 1) % MAX_PENDING;
        return;
    }

    if (dns_count >= MAX_DNS_PER_REPORT) return;

    int questions = (msg[4] << 8) | msg[5], answers = (msg[6] << 8) | msg[7], rcode = msg[3] & 0x0f;
    if (questions < 1) return;

    size_t pos = 12;
    char name[256];
    if (!read_name(msg, len, &pos, name, sizeof name) || pos + 4 > len) return;
    pos += 4;
    for (int q = 1; q < questions; q++)
    {
        char skip[256];
        if (!read_name(msg, len, &pos, skip, sizeof skip) || pos + 4 > len) return;
        pos += 4;
    }

    char ips[300] = "", aliases[300] = "";
    size_t ips_len = 0, aliases_len = 0;
    for (int a = 0; a < answers && pos < len; a++)
    {
        char skip[256];
        if (!read_name(msg, len, &pos, skip, sizeof skip) || pos + 10 > len) break;
        int type = (msg[pos] << 8) | msg[pos + 1], rdlength = (msg[pos + 8] << 8) | msg[pos + 9];
        pos += 10;
        if (pos + rdlength > len) break;

        char ip[256] = "";
        if (type == 5)
        {
            // CNAME: the name is an alias of this one; replies list the chain in order.
            char alias[256];
            size_t at = pos;
            if (read_name(msg, len, &at, alias, sizeof alias) && aliases_len + strlen(alias) + 2 < sizeof aliases)
            {
                aliases_len += snprintf(aliases + aliases_len, sizeof aliases - aliases_len, "%s%s", aliases_len ? "," : "", alias);
            }
        }
        else if (type == 1 && rdlength == 4) inet_ntop(AF_INET, msg + pos, ip, sizeof ip);
        else if (type == 28 && rdlength == 16) inet_ntop(AF_INET6, msg + pos, ip, sizeof ip);
        else if (type == 12)
        {
            // PTR: the name a reverse lookup returned.
            size_t at = pos;
            if (!read_name(msg, len, &at, ip, sizeof ip)) ip[0] = '\0';
        }
        if (*ip && ips_len + strlen(ip) + 2 < sizeof ips)
        {
            ips_len += snprintf(ips + ips_len, sizeof ips - ips_len, "%s%s", ips_len ? "," : "", ip);
        }

        pos += rdlength;
    }

    char took[24] = "-";
    uint16_t id = (uint16_t)((msg[0] << 8) | msg[1]);
    uint32_t h = hash_name(name);
    for (int i = 0; i < MAX_PENDING; i++)
    {
        if (pending[i].sent_us && pending[i].id == id && pending[i].name_hash == h)
        {
            snprintf(took, sizeof took, "%.1f", (now_us() - pending[i].sent_us) / 1000.0);
            pending[i].sent_us = 0;
            break;
        }
    }

    snprintf(dns_lines[dns_count++], sizeof dns_lines[0], "D %s %s %s %s %s\n",
             name, rcode < 6 ? rcodes[rcode] : "ERROR", ips_len ? ips : "-", took, aliases_len ? aliases : "-");
}

static void report(time_t now)
{
    int idle = 0;
    for (int i = 0; i < MAX_FLOWS; i++)
    {
        struct flow *f = &flows[i];
        if (!f->used) continue;
        if (now - f->last_seen > IDLE_SECONDS) idle = 1;
        if (!f->active) continue;

        char local[INET6_ADDRSTRLEN], remote[INET6_ADDRSTRLEN];
        int family = f->family == 6 ? AF_INET6 : AF_INET;
        inet_ntop(family, family == AF_INET6 ? (void *)f->local : (void *)(f->local + 12), local, sizeof local);
        inet_ntop(family, family == AF_INET6 ? (void *)f->remote : (void *)(f->remote + 12), remote, sizeof remote);
        const char *proto = f->proto == IPPROTO_TCP ? "tcp" : f->proto == IPPROTO_UDP ? "udp"
                          : (f->proto == IPPROTO_ICMP || f->proto == IPPROTO_ICMPV6) ? "icmp" : "ip";
        char rtt[24] = "-", status[8] = "-", response[24] = "-";
        if (f->connecting && f->handshake_us && !f->established && now_us() - f->handshake_us > UNANSWERED_US) f->unanswered = 1;
        if (f->rtt_us) snprintf(rtt, sizeof rtt, "%.1f", f->rtt_us / 1000.0);
        if (f->http_status) snprintf(status, sizeof status, "%u", f->http_status);
        if (f->response_us) snprintf(response, sizeof response, "%.1f", f->response_us / 1000.0);
#define OR_DASH(s) ((s)[0] ? (s) : "-")
        printf("F %s %s %u %s %u %llu %llu %llu %llu %s %u %u %s %s %s %s %s %s %s %s %s %u %u %u %u %u %u %u %u\n", proto, local, f->local_port, remote, f->remote_port,
               (unsigned long long)f->bytes_out, (unsigned long long)f->bytes_in,
               (unsigned long long)f->packets_out, (unsigned long long)f->packets_in,
               rtt, f->retrans_out, f->retrans_in,
               OR_DASH(f->tls_name), OR_DASH(f->http_method), OR_DASH(f->http_host), OR_DASH(f->http_path),
               status, response, OR_DASH(f->tls_version), OR_DASH(f->alpn), OR_DASH(f->alpn_offered),
               f->resets_out, f->resets_in, f->refused, f->unanswered, f->zero_window_out, f->zero_window_in, f->unreachable, f->too_big);
        f->active = 0;
    }

    for (int i = 0; i < exchange_count; i++)
    {
        struct exchange *e = &exchanges[i];
        struct flow *f = e->flow;
        if (!e->requests && !e->request_bytes && !e->response_bytes) continue;
        char remote[INET6_ADDRSTRLEN], operation[48];
        int family = f->family == 6 ? AF_INET6 : AF_INET;
        inet_ntop(family, family == AF_INET6 ? (void *)f->remote : (void *)(f->remote + 12), remote, sizeof remote);
        size_t n = 0;
        for (const char *c = e->operation; *c && n + 1 < sizeof operation; c++) operation[n++] = *c == ' ' ? '\t' : *c;
        operation[n] = '\0';
        printf("Q %s %s %s %u %u %s %u %u %llu %llu", e->protocol->name, e->client ? "client" : "server", remote, f->remote_port,
               f->local_port, n ? operation : "-", e->requests, e->errors,
               (unsigned long long)e->request_bytes, (unsigned long long)e->response_bytes);
        for (int b = 0; b < 16; b++) printf("%c%u", b ? ',' : ' ', e->histogram[b]);
        putchar('\n');
    }
    exchange_count = 0;

    for (int i = 0; i < dns_count; i++) fputs(dns_lines[i], stdout);
    dns_count = 0;

    if (overflow_active)
    {
        printf("O %llu %llu\n", (unsigned long long)overflow_out, (unsigned long long)overflow_in);
        overflow_active = 0;
    }

    puts(".");
    fflush(stdout);

    if (idle)
    {
        // The table again, without the idle flows. Only the slots in use are copied and cleared: writing the
        // whole table (and a second one to copy it to) would keep megabytes resident for a handful of flows.
        struct flow *kept = malloc((size_t)flow_count * sizeof *kept);
        if (!kept) return;   // tried again on the next report
        int kept_count = 0;
        exchange_count = 0;
        for (int i = 0; i < MAX_FLOWS; i++)
        {
            if (!flows[i].used) continue;
            if (now - flows[i].last_seen <= IDLE_SECONDS) kept[kept_count++] = flows[i];
            memset(&flows[i], 0, sizeof flows[i]);
        }

        flow_count = 0;
        for (int i = 0; i < kept_count; i++)
        {
            struct flow *f = find(&kept[i]);
            if (f) *f = kept[i];
        }

        free(kept);
    }
}

int main(void)
{
    int fd = socket(AF_PACKET, SOCK_DGRAM, htons(ETH_P_ALL));
    if (fd < 0)
    {
        fprintf(stderr, "packet socket: %s\n", strerror(errno));
        return 2;
    }

    struct sock_fprog filter = {sizeof filter_code / sizeof filter_code[0], filter_code};
    if (setsockopt(fd, SOL_SOCKET, SO_ATTACH_FILTER, &filter, sizeof filter) < 0)
    {
        fprintf(stderr, "filter: %s\n", strerror(errno));
        return 2;
    }

    int buffer = 4 * 1024 * 1024;
    setsockopt(fd, SOL_SOCKET, SO_RCVBUF, &buffer, sizeof buffer);
    unsigned int loopback = if_nametoindex("lo");

    uint8_t packet[2048];
    time_t last_report = time(NULL);
    int commands = 1;
    for (;;)
    {
        struct pollfd p[2] = {{fd, POLLIN, 0}, {commands ? STDIN_FILENO : -1, POLLIN, 0}};
        if (poll(p, 2, 250) > 0 && p[1].revents)
        {
            if (p[1].revents & (POLLHUP | POLLERR)) commands = 0;
            else read_command();
        }
        // Every packet waiting, up to a burst, on the one poll: under load that is one system call a packet, not two.
        for (int burst = 0; burst < MAX_BURST && (p[0].revents & POLLIN); burst++)
        {
            struct sockaddr_ll from;
            socklen_t from_len = sizeof from;
            // MSG_TRUNC returns the packet's full length even though only its headers were copied.
            ssize_t full = recvfrom(fd, packet, sizeof packet, MSG_TRUNC | (burst ? MSG_DONTWAIT : 0), (struct sockaddr *)&from, &from_len);
            if (full <= 0) break;
            {
                size_t captured = (size_t)full < sizeof packet ? (size_t)full : sizeof packet;
                int outgoing = from.sll_pkttype == PACKET_OUTGOING;
                int on_loopback = (unsigned int)from.sll_ifindex == loopback;
                if (!(on_loopback && outgoing)) // loopback shows each packet twice
                {
                    if (capture_until_us)
                    {
                        if (now_us() < capture_until_us && captured_packets < MAX_CAPTURED) capture(packet, captured, (size_t)full);
                        else { printf("C %ld\n", captured_packets); fflush(stdout); capture_until_us = 0; }
                    }
                    struct flow key = {0};
                    const uint8_t *transport = NULL;
                    size_t transport_len = 0;
                    if (ntohs(from.sll_protocol) == ETH_P_IP && captured >= 20)
                    {
                        size_t header = (packet[0] & 0x0f) * 4;
                        key.family = 4;
                        key.proto = packet[9];
                        memcpy((outgoing ? key.local : key.remote) + 12, packet + 12, 4);
                        memcpy((outgoing ? key.remote : key.local) + 12, packet + 16, 4);
                        int fragment = (((packet[6] & 0x1f) << 8) | packet[7]) != 0;
                        if (!fragment && captured > header) { transport = packet + header; transport_len = captured - header; }
                    }
                    else if (ntohs(from.sll_protocol) == ETH_P_IPV6 && captured >= 40)
                    {
                        key.family = 6;
                        key.proto = packet[6];
                        memcpy(outgoing ? key.local : key.remote, packet + 8, 16);
                        memcpy(outgoing ? key.remote : key.local, packet + 24, 16);
                        transport = packet + 40;
                        transport_len = captured - 40;
                    }

                    if (key.family)
                    {
                        if ((key.proto == IPPROTO_TCP || key.proto == IPPROTO_UDP) && transport && transport_len >= 4)
                        {
                            uint16_t source = (transport[0] << 8) | transport[1], destination = (transport[2] << 8) | transport[3];
                            key.local_port = outgoing ? source : destination;
                            key.remote_port = outgoing ? destination : source;

                            int resolver = key.family == 4
                                && (memcmp(packet + 12, "\x7f\x00\x00\x0b", 4) == 0 || memcmp(packet + 16, "\x7f\x00\x00\x0b", 4) == 0);
                            if (key.proto == IPPROTO_UDP && (source == 53 || destination == 53 || resolver) && transport_len > 8)
                            {
                                parse_dns(transport + 8, transport_len - 8);
                            }
                        }

                        if (!on_loopback && !outgoing && transport && transport_len >= 8
                            && (key.proto == IPPROTO_ICMP || key.proto == IPPROTO_ICMPV6))
                        {
                            track_icmp(key.family, transport, transport_len);
                        }

                        struct flow *f = on_loopback ? NULL : count(&key, outgoing, (uint64_t)full, time(NULL));
                        if (f && key.proto == IPPROTO_TCP && transport && transport_len >= 20)
                        {
                            // The payload's real length, from the IP header (only part of it may be captured).
                            long ip_payload = key.family == 4 ? (long)be16(packet + 2) - (packet[0] & 0x0f) * 4 : (long)be16(packet + 4);
                            long tcp_header = (transport[12] >> 4) * 4;
                            size_t payload_len = ip_payload > tcp_header ? (size_t)(ip_payload - tcp_header) : 0;
                            track_tcp(f, outgoing, transport, transport_len, payload_len);
                        }
                    }
                }
            }
        }

        if (capture_until_us && now_us() >= capture_until_us)
        {
            printf("C %ld\n", captured_packets);
            fflush(stdout);
            capture_until_us = 0;
        }

        time_t now = time(NULL);
        if (now != last_report)
        {
            report(now);
            last_report = now;
        }
    }
}
