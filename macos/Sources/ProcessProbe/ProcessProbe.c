#include "ProcessProbe.h"
#include <libproc.h>
#include <mach/mach.h>

GuardProcessIdentity guard_process_identity(int32_t pid) {
    GuardProcessIdentity result = {0};
    struct proc_bsdinfo info = {0};
    int size = proc_pidinfo(pid, PROC_PIDTBSDINFO, 0, &info, sizeof(info));
    if (size != sizeof(info) || info.pbi_start_tvsec == 0) return result;
    result.pid = pid;
    result.parent_pid = info.pbi_ppid;
    result.user_id = info.pbi_uid;
    result.started_seconds = info.pbi_start_tvsec;
    result.started_microseconds = info.pbi_start_tvusec;
    result.valid = 1;
    return result;
}

uint64_t guard_available_memory(void) {
    vm_statistics64_data_t stats = {0};
    mach_msg_type_number_t count = HOST_VM_INFO64_COUNT;
    vm_size_t page_size = 0;
    mach_port_t host = mach_host_self();
    kern_return_t pages = host_page_size(host, &page_size);
    kern_return_t read = host_statistics64(host, HOST_VM_INFO64, (host_info64_t)&stats, &count);
    mach_port_deallocate(mach_task_self(), host);
    if (pages != KERN_SUCCESS || read != KERN_SUCCESS) return 0;
    // Inactive and speculative pages are reclaimable; this is an estimate, not Activity Monitor's pressure metric.
    return ((uint64_t)stats.free_count + stats.inactive_count + stats.speculative_count) * page_size;
}
