// This helper is exercised in a separate process, never inside the test host.
// Signal callbacks touch only sig_atomic_t counters; no managed/Objective-C work.
#define _DARWIN_C_SOURCE 1
#include <signal.h>
#include <stddef.h>
#include <stdint.h>
#include <string.h>

_Static_assert(sizeof(void *) == 8, "probe requires a 64-bit Apple target");
_Static_assert(sizeof(sigset_t) == 4, "Darwin sigset_t ABI changed");
_Static_assert(sizeof(struct sigaction) == 16, "Darwin sigaction ABI changed");
_Static_assert(offsetof(struct sigaction, sa_mask) == 8, "Darwin mask offset changed");
_Static_assert(offsetof(struct sigaction, sa_flags) == 12, "Darwin flags offset changed");
_Static_assert(SA_SIGINFO == 0x40, "Darwin SA_SIGINFO changed");
_Static_assert(SIGBUS == 10 && SIGSEGV == 11, "Darwin signal numbers changed");

static struct sigaction saved_segv, saved_bus;
static volatile sig_atomic_t replacement_segv, replacement_bus, old_segv, old_bus, invalid_info;
static int begun;

static void old_segv_handler(int signal) { if (signal == SIGSEGV) old_segv++; }
static void old_bus_handler(int signal, siginfo_t *info, void *context) {
    if (signal == SIGBUS) old_bus++;
    if (!info || !context || info->si_signo != signal) invalid_info++;
}
static void replacement_handler(int signal, siginfo_t *info, void *context) {
    if (signal == SIGSEGV) replacement_segv++;
    if (signal == SIGBUS) replacement_bus++;
    if (!info || !context || info->si_signo != signal) invalid_info++;
}

int64_t probe_layout(int field) {
    switch (field) {
    case 0: return sizeof(struct sigaction);
    case 1: return sizeof(sigset_t);
    case 2: return offsetof(struct sigaction, sa_mask);
    case 3: return offsetof(struct sigaction, sa_flags);
    case 4: return SA_SIGINFO;
    default: return -1;
    }
}

void *probe_replacement_handler(void) { return (void *)replacement_handler; }

int probe_begin(void) {
    struct sigaction segv = {0}, bus = {0};
    segv.sa_handler = old_segv_handler;
    segv.sa_flags = SA_RESTART;
    sigemptyset(&segv.sa_mask);
    sigaddset(&segv.sa_mask, SIGUSR1);
    bus.sa_sigaction = old_bus_handler;
    bus.sa_flags = SA_SIGINFO | SA_RESTART;
    sigemptyset(&bus.sa_mask);
    sigaddset(&bus.sa_mask, SIGUSR2);
    if (sigaction(SIGSEGV, &segv, &saved_segv)) return 1;
    if (sigaction(SIGBUS, &bus, &saved_bus)) {
        sigaction(SIGSEGV, &saved_segv, NULL);
        return 2;
    }
    begun = 1;
    return 0;
}

int probe_end(void) {
    if (!begun) return 0;
    int segv = sigaction(SIGSEGV, &saved_segv, NULL);
    int bus = sigaction(SIGBUS, &saved_bus, NULL);
    begun = 0;
    return segv || bus;
}

int probe_current_flags(int signal) {
    struct sigaction current = {0};
    if (sigaction(signal, NULL, &current)) return -1;
    return current.sa_flags;
}

// Negative control: exactly the old managed byte layout (flags at 136). The
// public Darwin libc consumes flags at 12, so the registered flags become zero.
// Never deliver a signal to this intentionally malformed registration.
int probe_legacy_layout_flags(void) {
    _Alignas(struct sigaction) unsigned char legacy[148] = {0};
    void *handler = (void *)replacement_handler;
    int linux_siginfo = 4;
    struct sigaction old = {0};
    memcpy(legacy, &handler, sizeof(handler));
    memcpy(legacy + 136, &linux_siginfo, sizeof(linux_siginfo));
    if (sigaction(SIGSEGV, (const struct sigaction *)(const void *)legacy, &old)) return -1;
    int flags = probe_current_flags(SIGSEGV);
    if (sigaction(SIGSEGV, &old, NULL)) return -2;
    return flags;
}

int probe_raise_replacement(void) {
    if (raise(SIGSEGV) || raise(SIGBUS)) return 1;
    if (replacement_segv != 1 || replacement_bus != 1 || invalid_info) return 2;
    return old_segv || old_bus ? 3 : 0;
}

int probe_check_restored_and_raise(void) {
    struct sigaction segv = {0}, bus = {0};
    if (sigaction(SIGSEGV, NULL, &segv) || sigaction(SIGBUS, NULL, &bus)) return 1;
    if (segv.sa_handler != old_segv_handler || segv.sa_flags != SA_RESTART ||
        !sigismember(&segv.sa_mask, SIGUSR1) || sigismember(&segv.sa_mask, SIGUSR2)) return 2;
    if (bus.sa_sigaction != old_bus_handler || bus.sa_flags != (SA_SIGINFO | SA_RESTART) ||
        !sigismember(&bus.sa_mask, SIGUSR2) || sigismember(&bus.sa_mask, SIGUSR1)) return 3;
    if (raise(SIGSEGV) || raise(SIGBUS)) return 4;
    return old_segv == 1 && old_bus == 1 && invalid_info == 0 ? 0 : 5;
}
