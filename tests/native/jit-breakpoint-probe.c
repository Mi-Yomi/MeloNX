#define _DARWIN_C_SOURCE 1
#include <signal.h>
#include <stdint.h>
#include <string.h>
#include <sys/ucontext.h>

#if !defined(__arm64__)
#error This native JIT breakpoint probe requires Apple arm64.
#endif

// Match the small, bundled BreakpointJIT instruction sequences exactly.
__attribute__((naked, noinline)) void *BreakGetJITMapping(void *address __attribute__((unused)), size_t size __attribute__((unused))) {
    __asm__ volatile("mov x16, #1\n brk #0xf00d\n ret");
}
__attribute__((naked, noinline)) void BreakJITDetach(void) {
    __asm__ volatile("mov x16, #0\n brk #0xf00d\n ret");
}
__attribute__((naked, noinline)) void *BreakMarkJITMapping(size_t size __attribute__((unused))) {
    __asm__ volatile("brk #0x69\n ret");
}

static volatile sig_atomic_t trap_count, bus_count, invalid_context;
static struct sigaction saved_trap, saved_bus;
static int begun;

static void previous_trap(int signal) { if (signal == SIGTRAP) trap_count++; }
static void previous_bus(int signal, siginfo_t *info, void *context) {
    if (signal == SIGBUS) bus_count++;
    if (!info || !context || info->si_signo != signal) invalid_context++;
}

int jit_probe_begin(void) {
    struct sigaction trap = {0}, bus = {0};
    trap.sa_handler = previous_trap;
    trap.sa_flags = SA_RESTART;
    bus.sa_sigaction = previous_bus;
    bus.sa_flags = SA_SIGINFO;
    if (sigaction(SIGTRAP, &trap, &saved_trap)) return 1;
    if (sigaction(SIGBUS, &bus, &saved_bus)) {
        sigaction(SIGTRAP, &saved_trap, NULL);
        return 2;
    }
    begun = 1;
    return 0;
}

int jit_probe_end(void) {
    if (!begun) return 0;
    int trap = sigaction(SIGTRAP, &saved_trap, NULL);
    int bus = sigaction(SIGBUS, &saved_bus, NULL);
    begun = 0;
    return trap || bus;
}

int jit_probe_protocol(void) {
    if (BreakGetJITMapping(NULL, 4096) != NULL) return 1;
    if (BreakMarkJITMapping(4096) != NULL) return 2;
    BreakJITDetach();
    return trap_count || bus_count || invalid_context ? 3 : 0;
}

int jit_probe_unrelated_delivery(void) {
    if (raise(SIGTRAP) || raise(SIGBUS)) return 1;
    return trap_count == 1 && bus_count == 1 && !invalid_context ? 0 : 2;
}

// Exercise the production Swift callback with an exact synthetic Darwin context.
// Unrelated PCs (even one instruction next to the known BRK) must stay untouched.
int jit_probe_context(void (*handler)(int, siginfo_t *, void *), int known) {
    struct __darwin_mcontext64 machine = {0};
    ucontext_t context = {0};
    siginfo_t info = {0};
    uint64_t pc = (uint64_t)(uintptr_t)BreakGetJITMapping + (known ? 4 : 8);
    machine.__ss.__pc = pc;
    machine.__ss.__x[0] = 0xfeed;
    context.uc_mcontext = &machine;
    info.si_signo = SIGBUS;
    handler(SIGBUS, &info, &context);
    if (known) return machine.__ss.__pc == pc + 4 && machine.__ss.__x[0] == 0 ? 0 : 1;
    return machine.__ss.__pc == pc && machine.__ss.__x[0] == 0xfeed && bus_count == 2 ? 0 : 2;
}
