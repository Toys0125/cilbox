/*
 * Deterministic tests of the exact retry policy used by the Windows DLL.
 * No Windows APIs, timing assumptions, sleeps, or hardware qualification.
 */
#include <stdint.h>
#include <stdio.h>
#include <inttypes.h>
#include "../Packages/com.cnlohr.cilbox/Native~/CilboxCpuClockRetry.h"

typedef struct {
    int status;
    uint64_t candidate;
} attempt_result;

typedef struct {
    const char* name;
    attempt_result results[4];
    unsigned int result_count;
    int expected_status;
    unsigned int expected_attempts;
    unsigned int expected_waits;
    uint64_t expected_frequency;
} scenario;

typedef struct {
    const scenario* test;
    uint64_t* published_frequency;
    unsigned int attempts;
    unsigned int waits;
    int candidates_started_zero;
    int unpublished_during_attempts;
    int valid_waits;
} fixture;

static int injected_attempt(void* context, uint64_t* candidate)
{
    fixture* state = (fixture*)context;
    unsigned int index = state->attempts++;
    if (*candidate != 0) state->candidates_started_zero = 0;
    if (*state->published_frequency != 0) state->unpublished_during_attempts = 0;
    /* Terminate even a broken retry loop instead of hanging the test process. */
    if (index >= state->test->result_count)
        return CILBOX_CLOCK_NOT_PREPARED;
    *candidate = state->test->results[index].candidate;
    return state->test->results[index].status;
}

static void injected_wait(void* context, unsigned int delay_ms)
{
    fixture* state = (fixture*)context;
    ++state->waits;
    if (delay_ms != 20 || state->attempts != state->waits
        || *state->published_frequency != 0)
        state->valid_waits = 0;
}

static int run(const scenario* test)
{
    uint64_t frequency = UINT64_C(987654321);
    fixture state = { test, &frequency, 0, 0, 1, 1, 1 };
    int status = cilbox_clock_qualify_bounded(
        injected_attempt, injected_wait, &state, &frequency);
    int passed = status == test->expected_status
        && frequency == test->expected_frequency
        && state.attempts == test->expected_attempts
        && state.waits == test->expected_waits
        && state.candidates_started_zero
        && state.unpublished_during_attempts
        && state.valid_waits;
    printf("%s %s\n", passed ? "PASS" : "FAIL", test->name);
    if (!passed) {
        printf("  status=%d expected=%d, frequency=%" PRIu64 " expected=%" PRIu64
            ", attempts=%u expected=%u, waits=%u expected=%u\n",
            status, test->expected_status, frequency, test->expected_frequency,
            state.attempts, test->expected_attempts, state.waits, test->expected_waits);
        printf("  zeroed_candidates=%d unpublished_until_success=%d valid_wait_order_and_delay=%d\n",
            state.candidates_started_zero, state.unpublished_during_attempts, state.valid_waits);
    }
    return passed;
}

int main(void)
{
    static const scenario tests[] = {
        {
            "first_success_publishes_frequency_without_waiting",
            { { CILBOX_CLOCK_OK, UINT64_C(2400000000) } }, 1,
            CILBOX_CLOCK_OK, 1, 0, UINT64_C(2400000000)
        },
        {
            "transient_status_5_then_success_retries_once",
            { { CILBOX_CLOCK_THREAD_CYCLES_FAILED_QUALIFICATION, 111 },
              { CILBOX_CLOCK_OK, UINT64_C(2400000000) } }, 2,
            CILBOX_CLOCK_OK, 2, 1, UINT64_C(2400000000)
        },
        {
            "transient_query_failure_then_success_retries_once",
            { { CILBOX_CLOCK_QUERY_FAILED, 111 },
              { CILBOX_CLOCK_OK, UINT64_C(2400000000) } }, 2,
            CILBOX_CLOCK_OK, 2, 1, UINT64_C(2400000000)
        },
        {
            "transient_unstable_frequency_then_success_retries_once",
            { { CILBOX_CLOCK_UNSTABLE_FREQUENCY, 111 },
              { CILBOX_CLOCK_OK, UINT64_C(2400000000) } }, 2,
            CILBOX_CLOCK_OK, 2, 1, UINT64_C(2400000000)
        },
        {
            "success_on_third_attempt_uses_only_its_qualified_candidate",
            { { CILBOX_CLOCK_QUERY_FAILED, 111 },
              { CILBOX_CLOCK_UNSTABLE_FREQUENCY, 222 },
              { CILBOX_CLOCK_OK, UINT64_C(2400000000) } }, 3,
            CILBOX_CLOCK_OK, 3, 2, UINT64_C(2400000000)
        },
        {
            "unsupported_architecture_stops_without_retry",
            { { CILBOX_CLOCK_UNSUPPORTED_ARCHITECTURE, 111 },
              { CILBOX_CLOCK_OK, UINT64_C(2400000000) } }, 2,
            CILBOX_CLOCK_UNSUPPORTED_ARCHITECTURE, 1, 0, 0
        },
        {
            "missing_invariant_tsc_stops_without_retry",
            { { CILBOX_CLOCK_NO_INVARIANT_TSC, 111 },
              { CILBOX_CLOCK_OK, UINT64_C(2400000000) } }, 2,
            CILBOX_CLOCK_NO_INVARIANT_TSC, 1, 0, 0
        },
        {
            "three_status_5_failures_stop_before_fourth_success",
            { { CILBOX_CLOCK_THREAD_CYCLES_FAILED_QUALIFICATION, 111 },
              { CILBOX_CLOCK_THREAD_CYCLES_FAILED_QUALIFICATION, 222 },
              { CILBOX_CLOCK_THREAD_CYCLES_FAILED_QUALIFICATION, 333 },
              { CILBOX_CLOCK_OK, UINT64_C(2400000000) } }, 4,
            CILBOX_CLOCK_THREAD_CYCLES_FAILED_QUALIFICATION, 3, 2, 0
        },
        {
            "exhausted_mixed_transients_report_last_failure_without_frequency",
            { { CILBOX_CLOCK_QUERY_FAILED, 111 },
              { CILBOX_CLOCK_UNSTABLE_FREQUENCY, 222 },
              { CILBOX_CLOCK_THREAD_CYCLES_FAILED_QUALIFICATION, 333 },
              { CILBOX_CLOCK_OK, UINT64_C(2400000000) } }, 4,
            CILBOX_CLOCK_THREAD_CYCLES_FAILED_QUALIFICATION, 3, 2, 0
        },
        {
            "permanent_failure_after_transient_stops_without_another_wait",
            { { CILBOX_CLOCK_THREAD_CYCLES_FAILED_QUALIFICATION, 111 },
              { CILBOX_CLOCK_NO_INVARIANT_TSC, 222 },
              { CILBOX_CLOCK_OK, UINT64_C(2400000000) } }, 3,
            CILBOX_CLOCK_NO_INVARIANT_TSC, 2, 1, 0
        },
        {
            "unknown_status_is_not_retried_or_published",
            { { 99, 111 }, { CILBOX_CLOCK_OK, UINT64_C(2400000000) } }, 2,
            99, 1, 0, 0
        },
        {
            "not_prepared_status_is_not_treated_as_a_retryable_measurement",
            { { CILBOX_CLOCK_NOT_PREPARED, 111 },
              { CILBOX_CLOCK_OK, UINT64_C(2400000000) } }, 2,
            CILBOX_CLOCK_NOT_PREPARED, 1, 0, 0
        },
        {
            "success_without_frequency_is_retried_as_query_failure",
            { { CILBOX_CLOCK_OK, 0 },
              { CILBOX_CLOCK_OK, UINT64_C(2400000000) } }, 2,
            CILBOX_CLOCK_OK, 2, 1, UINT64_C(2400000000)
        },
        {
            "three_zero_frequency_successes_exhaust_as_query_failure",
            { { CILBOX_CLOCK_OK, 0 }, { CILBOX_CLOCK_OK, 0 },
              { CILBOX_CLOCK_OK, 0 }, { CILBOX_CLOCK_OK, UINT64_C(2400000000) } }, 4,
            CILBOX_CLOCK_QUERY_FAILED, 3, 2, 0
        }
    };
    unsigned int total = (unsigned int)(sizeof(tests) / sizeof(tests[0]));
    unsigned int passed = 0;
    for (unsigned int i = 0; i < total; ++i) passed += (unsigned int)run(&tests[i]);
    printf("Qualification policy: %u passed, %u failed; hardware checks not exercised.\n",
        passed, total - passed);
    return passed == total ? 0 : 1;
}
