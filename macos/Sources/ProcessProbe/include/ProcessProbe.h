#ifndef AI_RAM_GUARD_PROCESS_PROBE_H
#define AI_RAM_GUARD_PROCESS_PROBE_H
#include <stdint.h>
typedef struct {
    int32_t pid;
    int32_t parent_pid;
    uint32_t user_id;
    uint64_t started_seconds;
    uint64_t started_microseconds;
    int valid;
} GuardProcessIdentity;
GuardProcessIdentity guard_process_identity(int32_t pid);
uint64_t guard_available_memory(void);
#endif
