#ifndef CILBOX_CPU_CLOCK_RETRY_H
#define CILBOX_CPU_CLOCK_RETRY_H

#include <stdint.h>

enum {
    CILBOX_CLOCK_OK = 0,
    CILBOX_CLOCK_UNSUPPORTED_ARCHITECTURE = 1,
    CILBOX_CLOCK_NO_INVARIANT_TSC = 2,
    CILBOX_CLOCK_QUERY_FAILED = 3,
    CILBOX_CLOCK_UNSTABLE_FREQUENCY = 4,
    CILBOX_CLOCK_THREAD_CYCLES_FAILED_QUALIFICATION = 5,
    CILBOX_CLOCK_NOT_PREPARED = 6,
    CILBOX_CLOCK_MAX_INITIALIZATION_ATTEMPTS = 3,
    CILBOX_CLOCK_RETRY_DELAY_MS = 20
};

typedef int (*cilbox_clock_attempt_fn)(void* context, uint64_t* candidate);
typedef void (*cilbox_clock_retry_wait_fn)(void* context, unsigned int delay_ms);

/*
 * Shared production policy; intentionally independent of Windows so injected
 * status sequences can exercise the real retry logic in portable tests.
 *
 * The attempt validates the platform/clock and writes a frequency on success.
 * A failed attempt's candidate is never published or reused. Success without a
 * frequency is a retryable query failure. Only statuses 3/4/5 are retryable.
 * Three TOTAL attempts means at most two waits, never a wait after termination.
 */
static int cilbox_clock_qualify_bounded(cilbox_clock_attempt_fn attempt,
    cilbox_clock_retry_wait_fn retry_wait, void* context, uint64_t* out_frequency)
{
    if (!out_frequency) return CILBOX_CLOCK_QUERY_FAILED;
    *out_frequency = 0;
    if (!attempt) return CILBOX_CLOCK_QUERY_FAILED;

    int status = CILBOX_CLOCK_QUERY_FAILED;
    for (int index = 0; index < CILBOX_CLOCK_MAX_INITIALIZATION_ATTEMPTS; ++index) {
        uint64_t candidate = 0;
        status = attempt(context, &candidate);
        if (status == CILBOX_CLOCK_OK) {
            if (candidate != 0) {
                *out_frequency = candidate;
                return CILBOX_CLOCK_OK;
            }
            status = CILBOX_CLOCK_QUERY_FAILED;
        }
        if (status != CILBOX_CLOCK_QUERY_FAILED
            && status != CILBOX_CLOCK_UNSTABLE_FREQUENCY
            && status != CILBOX_CLOCK_THREAD_CYCLES_FAILED_QUALIFICATION)
            return status;

        if (index + 1 < CILBOX_CLOCK_MAX_INITIALIZATION_ATTEMPTS && retry_wait)
            retry_wait(context, CILBOX_CLOCK_RETRY_DELAY_MS);
    }
    return status;
}

#endif
