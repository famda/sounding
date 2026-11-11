// /opt/sounding/run: starts a container's command with the Sounding agent beside it.
//
//   ENTRYPOINT ["/opt/sounding/run", "<the image's entrypoint>"]
//
// Signals go to the command; when it exits the agent is stopped and the launcher exits with the command's
// status, reaping orphans meanwhile as PID 1. Before stopping the agent it writes how the command ended to a
// pipe the agent inherits (SOUNDING_EXIT_FD): "<exit code|-1> <signal|0> <ms since the last signal|-1>".
// Without a command it runs the agent alone.
//
// Unless the container has a terminal, the command's stdout and stderr go through the launcher, which copies
// them unchanged and once a second tells the agent (SOUNDING_LOG_FD) how many lines, errors, warnings and bytes
// there were, and that second's last lines:
//   S <lines> <errors> <warnings> <bytes>
//   L <o|e> <e|w|i> <text>
//
// Linked statically, and the agent brings its own musl loader and OpenSSL: nothing is needed from the image.

#include <ctype.h>
#include <errno.h>
#include <fcntl.h>
#include <poll.h>
#include <signal.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/wait.h>
#include <time.h>
#include <unistd.h>

#define BUNDLE "/opt/sounding"
#define AGENT BUNDLE "/agent"
#define STOP_GRACE_MS 5000
#define STOP_WINDOW_MS 300000
#define LINE_MAX_KEPT 200
#define LINES_PER_REPORT 20

static volatile pid_t app = -1;
static volatile sig_atomic_t signalled;
static struct timespec last_signal;

static void forward(int sig)
{
    clock_gettime(CLOCK_MONOTONIC, &last_signal);
    signalled = 1;
    if (app > 0) kill(app, sig);
}

static int forwarded(int sig)
{
    switch (sig) {
    case SIGKILL: case SIGSTOP: case SIGCHLD: case SIGPIPE:
    case SIGSEGV: case SIGBUS: case SIGFPE: case SIGILL: case SIGSYS: case SIGTRAP:
        return 0;
    default:
        return 1;
    }
}

static void exec_agent(void)
{
    setenv("SOUNDING_LOGGING__LOGLEVEL__DEFAULT", "Warning", 0);
    setenv("SOUNDING_LOGGING__LOGLEVEL__MICROSOFT.HOSTING.LIFETIME", "Warning", 0);
    if (chdir(AGENT) != 0) {
        fprintf(stderr, "sounding-agent: %s: %s\n", AGENT, strerror(errno));
        _exit(127);
    }
    execl(AGENT "/sounding-agent", "sounding-agent", "--library-path", AGENT, AGENT "/sounding-agent.elf", (char *)NULL);
    fprintf(stderr, "sounding-agent: cannot start the agent: %s\n", strerror(errno));
    _exit(127);
}

struct stream
{
    int from, child, to;   // the launcher's end of the pipe, the command's end, and where the output goes
    char kind;             // 'o' or 'e'
    char line[LINE_MAX_KEPT + 1];
    size_t length;
};

static struct { unsigned long lines, errors, warnings, bytes; } counted;
static char recent[LINES_PER_REPORT][LINE_MAX_KEPT + 8];
static int recent_count, recent_next;

static int has_word(const char *text, const char *word)
{
    size_t n = strlen(word);
    for (const char *at = text; (at = strstr(at, word)); at++)
    {
        if ((at == text || !isalpha((unsigned char)at[-1])) && !isalpha((unsigned char)at[n])) return 1;
    }
    return 0;
}

static char level_of(const char *line)
{
    char lower[LINE_MAX_KEPT + 1];
    size_t i = 0;
    for (; line[i] && i < LINE_MAX_KEPT; i++) lower[i] = (char)tolower((unsigned char)line[i]);
    lower[i] = '\0';
    static const char *errors[] = {"error", "err", "fatal", "panic", "critical", "crit", "emerg", "alert", "exception", "traceback"};
    for (size_t e = 0; e < sizeof errors / sizeof errors[0]; e++) if (has_word(lower, errors[e])) return 'e';
    return has_word(lower, "warn") || has_word(lower, "warning") ? 'w' : 'i';
}

static void end_line(struct stream *s)
{
    s->line[s->length] = '\0';
    char level = level_of(s->line);
    counted.lines++;
    if (level == 'e') counted.errors++;
    if (level == 'w') counted.warnings++;
    snprintf(recent[recent_next], sizeof recent[0], "L %c %c %s\n", s->kind, level, s->line);
    recent_next = (recent_next + 1) % LINES_PER_REPORT;
    if (recent_count < LINES_PER_REPORT) recent_count++;
    s->length = 0;
}

// Copies what the command wrote, unchanged, and counts its lines: 1 when it copied something, 0 at the end of
// the output, -1 when nothing is waiting.
static int relay(struct stream *s)
{
    char buffer[16384];
    ssize_t n = read(s->from, buffer, sizeof buffer);
    if (n < 0) return errno == EINTR || errno == EAGAIN ? -1 : 0;
    if (n == 0) return 0;
    for (ssize_t done = 0; done < n;)
    {
        ssize_t w = write(s->to, buffer + done, (size_t)(n - done));
        if (w < 0) { if (errno == EINTR) continue; break; }
        done += w;
    }
    counted.bytes += (unsigned long)n;
    for (ssize_t i = 0; i < n; i++)
    {
        char c = buffer[i];
        if (c == '\n') end_line(s);
        else if (s->length < LINE_MAX_KEPT) s->line[s->length++] = (c == '\t' || (unsigned char)c >= ' ') ? c : ' ';
    }
    return 1;
}

static void report_logs(int fd)
{
    if (fd < 0 || (!counted.lines && !counted.bytes)) return;
    char text[LINES_PER_REPORT * (LINE_MAX_KEPT + 8) + 64];
    int n = snprintf(text, sizeof text, "S %lu %lu %lu %lu\n", counted.lines, counted.errors, counted.warnings, counted.bytes);
    for (int i = 0; i < recent_count; i++)
    {
        const char *line = recent[(recent_next - recent_count + i + LINES_PER_REPORT) % LINES_PER_REPORT];
        n += snprintf(text + n, sizeof text - (size_t)n, "%s", line);
    }
    if (write(fd, text, (size_t)n) < 0) { /* the agent is busy or gone: this second is dropped */ }
    memset(&counted, 0, sizeof counted);
    recent_count = 0;
}

static long long now_ms(void)
{
    struct timespec t;
    clock_gettime(CLOCK_MONOTONIC, &t);
    return t.tv_sec * 1000LL + t.tv_nsec / 1000000;
}

static pid_t start_agent(const sigset_t *mask, int exit_fd, int log_fd)
{
    pid_t pid = fork();
    if (pid != 0) return pid;

    sigprocmask(SIG_SETMASK, mask, NULL);
    if (exit_fd >= 0) {
        char fd[16];
        fcntl(exit_fd, F_SETFD, 0);
        snprintf(fd, sizeof fd, "%d", exit_fd);
        setenv("SOUNDING_EXIT_FD", fd, 1);
    }
    if (log_fd >= 0) {
        char fd[16];
        fcntl(log_fd, F_SETFD, 0);
        snprintf(fd, sizeof fd, "%d", log_fd);
        setenv("SOUNDING_LOG_FD", fd, 1);
    }
    exec_agent();
    return -1;
}

static pid_t start_app(char **argv, const sigset_t *mask, struct stream *streams, int relayed)
{
    pid_t pid = fork();
    if (pid != 0) return pid;

    sigprocmask(SIG_SETMASK, mask, NULL);
    for (int i = 0; i < relayed; i++) dup2(streams[i].child, streams[i].to);
    execvp(argv[0], argv);
    fprintf(stderr, "sounding-agent: cannot run %s: %s\n", argv[0], strerror(errno));
    _exit(errno == ENOENT ? 127 : 126);
}

static void stop_agent(pid_t agent)
{
    kill(agent, SIGTERM);
    struct timespec tick = { 0, 100 * 1000 * 1000 };
    for (int waited = 0; waited < STOP_GRACE_MS; waited += 100) {
        pid_t pid = waitpid(-1, NULL, WNOHANG);
        if (pid == agent || (pid < 0 && errno == ECHILD)) return;
        if (pid == 0) nanosleep(&tick, NULL);
    }
    kill(agent, SIGKILL);
    waitpid(agent, NULL, 0);
}

static void report_exit(int fd, int status)
{
    struct timespec now;
    clock_gettime(CLOCK_MONOTONIC, &now);
    long long ms = signalled ? (now.tv_sec - last_signal.tv_sec) * 1000LL + (now.tv_nsec - last_signal.tv_nsec) / 1000000 : -1;
    char line[64];
    int n = snprintf(line, sizeof line, "%d %d %lld\n", WIFEXITED(status) ? WEXITSTATUS(status) : -1,
        WIFSIGNALED(status) ? WTERMSIG(status) : 0, ms >= 0 && ms <= STOP_WINDOW_MS ? ms : -1);
    if (write(fd, line, (size_t)n) < 0) { /* the agent is gone */ }
}

int main(int argc, char **argv)
{
    if (argc < 2) exec_agent();

    setenv("CUDA_INJECTION64_PATH", BUNDLE "/libgpumeter.so", 0);

    // Signals wait until the command's pid is known; the children get the original mask back.
    sigset_t all, original;
    sigfillset(&all);
    sigprocmask(SIG_SETMASK, &all, &original);

    struct sigaction action = { 0 };
    action.sa_handler = forward;
    action.sa_flags = SA_RESTART;
    sigfillset(&action.sa_mask);
    for (int sig = 1; sig < NSIG; sig++) {
        if (forwarded(sig)) sigaction(sig, &action, NULL);
    }
    signal(SIGPIPE, SIG_IGN);

    int exit_pipe[2] = { -1, -1 };
    if (pipe2(exit_pipe, O_CLOEXEC) != 0) exit_pipe[0] = exit_pipe[1] = -1;

    // The command's output through the launcher, unless it writes to a terminal (docker run -t).
    struct stream streams[2] = { { -1, -1, STDOUT_FILENO, 'o', "", 0 }, { -1, -1, STDERR_FILENO, 'e', "", 0 } };
    int relayed = 0, log_pipe[2] = { -1, -1 };
    if (!isatty(STDOUT_FILENO) && !isatty(STDERR_FILENO) && pipe2(log_pipe, O_CLOEXEC) == 0) {
        fcntl(log_pipe[1], F_SETFL, O_NONBLOCK);
        for (; relayed < 2; relayed++) {
            int p[2];
            if (pipe2(p, O_CLOEXEC) != 0) break;
            streams[relayed].from = p[0];
            streams[relayed].child = p[1];
        }
        if (relayed < 2) {
            for (int i = 0; i < relayed; i++) { close(streams[i].from); close(streams[i].child); }
            close(log_pipe[0]);
            close(log_pipe[1]);
            relayed = 0;
            log_pipe[0] = log_pipe[1] = -1;
        }
    }

    pid_t agent = start_agent(&original, exit_pipe[0], log_pipe[0]);
    if (exit_pipe[0] >= 0) close(exit_pipe[0]);
    if (log_pipe[0] >= 0) close(log_pipe[0]);
    app = start_app(argv + 1, &original, streams, relayed);
    for (int i = 0; i < relayed; i++) close(streams[i].child);
    sigprocmask(SIG_SETMASK, &original, NULL);
    if (app < 0) {
        perror("sounding-agent: fork");
        if (agent > 0) stop_agent(agent);
        return 126;
    }

    int status = 0, open_streams = relayed;
    long long reported = now_ms();
    for (;;) {
        if (open_streams > 0) {
            struct pollfd fds[2];
            for (int i = 0; i < relayed; i++) fds[i] = (struct pollfd){ streams[i].from, POLLIN, 0 };
            if (poll(fds, (nfds_t)relayed, 250) > 0) {
                for (int i = 0; i < relayed; i++) {
                    if (streams[i].from >= 0 && fds[i].revents && relay(&streams[i]) == 0) {
                        if (streams[i].length) end_line(&streams[i]);
                        close(streams[i].from);
                        streams[i].from = -1;
                        open_streams--;
                    }
                }
            }
            if (now_ms() - reported >= 1000) {
                report_logs(log_pipe[1]);
                reported = now_ms();
            }
        }

        int st;
        pid_t pid = waitpid(-1, &st, open_streams > 0 ? WNOHANG : 0);
        if (pid == 0) continue;
        if (pid < 0) {
            if (errno == EINTR) continue;
            break;
        }
        if (pid == app) {
            status = WIFEXITED(st) ? WEXITSTATUS(st) : 128 + WTERMSIG(st);
            // What it wrote last: whatever is left in the pipes, without waiting for processes it left behind.
            for (int i = 0; i < relayed; i++) {
                if (streams[i].from < 0) continue;
                fcntl(streams[i].from, F_SETFL, O_NONBLOCK);
                while (relay(&streams[i]) > 0) { }
                if (streams[i].length) end_line(&streams[i]);
            }
            report_logs(log_pipe[1]);
            if (agent > 0 && exit_pipe[1] >= 0) report_exit(exit_pipe[1], st);
            break;
        }
        if (pid == agent) {
            agent = -1;
            fprintf(stderr, "sounding-agent: the agent stopped (%s %d); %s keeps running, unmonitored\n",
                WIFEXITED(st) ? "exit status" : "signal", WIFEXITED(st) ? WEXITSTATUS(st) : WTERMSIG(st), argv[1]);
        }
    }

    app = -1;
    if (agent > 0) stop_agent(agent);
    return status;
}
