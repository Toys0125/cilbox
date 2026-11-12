#define _POSIX_C_SOURCE 200809L
#include <stdint.h>
#include <time.h>
#include <stdlib.h>
static volatile uint64_t sink;
static uint64_t cpu_ns(void) {
    struct timespec value;
    if (clock_gettime(CLOCK_THREAD_CPUTIME_ID, &value) != 0) abort();
    return (uint64_t)value.tv_sec * UINT64_C(1000000000) + (uint64_t)value.tv_nsec;
}
__attribute__((visibility("default"))) void cilbox_budget_burn_ns(uint64_t duration) {
    uint64_t start = cpu_ns(), value = UINT64_C(0x123456789abcdef);
    do {
        for (int i = 0; i < 256; ++i) { value ^= value << 13; value ^= value >> 7; value ^= value << 17; }
    } while (cpu_ns() - start < duration);
    sink = value;
}
