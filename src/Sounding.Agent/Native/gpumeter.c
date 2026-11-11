// gpu-meter: per-process GPU usage, from inside every CUDA process in the container. The CUDA driver loads it
// when CUDA_INJECTION64_PATH points at it; CUPTI then reports kernels, CUDA Graphs, allocations and copies, and a
// callback sees allocation calls fail. Twice a second the totals go to $SOUNDING_GPU_METER_PATH/<pid> (default /tmp/gpu-meter):
//
//   v4 <visible GPUs> <peak bytes allocated> <GPU timestamp ns> <failed allocations>
//   <PCI bus id> <bytes allocated> <kernel busy ns> <bytes to GPU> <bytes to host> <launches> <kernel ns>   (per GPU used)
//   k <launches> <kernel ns> <kernel name>                                                                    (top kernels)
//
// Busy time is the union of kernel run times on the GPU's own clock; the reader divides it by the change of
// the report's GPU timestamp, so late delivery of the records doesn't skew it.

#include <cuda.h>
#include <cupti.h>
#include <errno.h>
#include <fcntl.h>
#include <pthread.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/stat.h>
#include <sys/random.h>
#include <time.h>
#include <unistd.h>

#define BUFFER_SIZE (4 * 1024 * 1024)
#define MAX_DEVICES 64
#define MAX_KERNELS 256       // distinct names kept; later ones aren't listed (their time still counts per device)
#define LISTED_KERNELS 32     // written per report, the ones with the most time
#define KERNEL_SLOTS 1024     // four slots a name, so a lookup rarely meets another name

struct device_usage
{
    int used;                          // any activity seen on this device
    unsigned long long busy_ns;        // union of kernel run time
    unsigned long long last_end;       // end of the latest kernel counted
    long long allocated_bytes;         // device + managed memory currently allocated
    unsigned long long to_device;      // bytes copied host -> device
    unsigned long long to_host;        // bytes copied device -> host
    unsigned long long launches;       // kernels and graph launches completed
    unsigned long long kernel_ns;      // their durations added up
};

struct kernel_usage
{
    char name[160];
    unsigned long long launches, ns;
};

static pthread_mutex_t lock = PTHREAD_MUTEX_INITIALIZER;
static struct device_usage devices[MAX_DEVICES];
static struct kernel_usage kernels[MAX_KERNELS];
static int kernel_count;
static long long peak_bytes;           // highest total allocation of the process
static unsigned long long failed_allocations;
static char path[32];
static int directory_fd = -1;
static CUpti_SubscriberHandle subscriber;

static void report(const char *what, CUptiResult result)
{
    const char *message = NULL;
    cuptiGetResultString(result, &message);
    fprintf(stderr, "[gpu-meter] %s: %s\n", what, message ? message : "unknown error");
}

static void count_busy(uint32_t device_id, uint64_t start, uint64_t end)
{
    struct device_usage *device = &devices[device_id % MAX_DEVICES];
    device->used = 1;
    device->launches++;
    device->kernel_ns += end > start ? end - start : 0;
    if (end > device->last_end)
    {
        device->busy_ns += end - (start > device->last_end ? start : device->last_end);
        device->last_end = end;
    }
}

// Where each name is in kernels, by its hash (the index plus one; 0 is an empty slot): a launch finds its kernel
// without comparing its name with every other.
static uint16_t kernel_slots[KERNEL_SLOTS];

static void count_kernel(const char *name, uint64_t start, uint64_t end)
{
    // A name is kept, and so told apart, by as much of it as fits: a longer one is the kernel its start names.
    const size_t kept = sizeof kernels[0].name - 1;
    uint32_t hash = 2166136261u;
    for (size_t n = 0; n < kept && name[n]; n++) hash = (hash ^ (unsigned char)name[n]) * 16777619u;
    uint32_t slot = hash % KERNEL_SLOTS;
    while (kernel_slots[slot] && strncmp(kernels[kernel_slots[slot] - 1].name, name, kept) != 0) slot = (slot + 1) % KERNEL_SLOTS;

    int i = kernel_slots[slot] - 1;
    if (i < 0)
    {
        if (kernel_count == MAX_KERNELS) return;
        i = kernel_count++;
        snprintf(kernels[i].name, sizeof kernels[i].name, "%s", name);
        kernel_slots[slot] = (uint16_t)(i + 1);
    }
    kernels[i].launches++;
    kernels[i].ns += end > start ? end - start : 0;
}

static void CUPTIAPI api_returned(void *unused, CUpti_CallbackDomain domain, CUpti_CallbackId id, const void *data)
{
    const CUpti_CallbackData *call = data;
    if (call->callbackSite == CUPTI_API_EXIT && call->functionReturnValue && *(const CUresult *)call->functionReturnValue != CUDA_SUCCESS)
    {
        __atomic_add_fetch(&failed_allocations, 1, __ATOMIC_RELAXED);
    }
}

static void CUPTIAPI buffer_requested(uint8_t **buffer, size_t *size, size_t *max_records)
{
    *buffer = aligned_alloc(8, BUFFER_SIZE);
    *size = *buffer ? BUFFER_SIZE : 0;
    *max_records = 0;
}

static void CUPTIAPI buffer_completed(CUcontext context, uint32_t stream, uint8_t *buffer, size_t size, size_t valid)
{
    CUpti_Activity *record = NULL;
    pthread_mutex_lock(&lock);
    while (cuptiActivityGetNextRecord(buffer, valid, &record) == CUPTI_SUCCESS)
    {
        if (record->kind == CUPTI_ACTIVITY_KIND_CONCURRENT_KERNEL)
        {
            CUpti_ActivityKernel9 *kernel = (CUpti_ActivityKernel9 *)record;
            count_busy(kernel->deviceId, kernel->start, kernel->end);
            count_kernel(kernel->name ? kernel->name : "unnamed kernel", kernel->start, kernel->end);
        }
        else if (record->kind == CUPTI_ACTIVITY_KIND_GRAPH_TRACE)
        {
            // A whole CUDA Graph launch (llama.cpp and ollama decode this way); its kernels aren't reported one by one.
            CUpti_ActivityGraphTrace2 *graph = (CUpti_ActivityGraphTrace2 *)record;
            count_busy(graph->deviceId, graph->start, graph->end);
            char name[32];
            snprintf(name, sizeof name, "CUDA Graph %u", graph->graphId);
            count_kernel(name, graph->start, graph->end);
        }
        else if (record->kind == CUPTI_ACTIVITY_KIND_MEMORY2)
        {
            CUpti_ActivityMemory4 *memory = (CUpti_ActivityMemory4 *)record;
            if (memory->memoryKind == CUPTI_ACTIVITY_MEMORY_KIND_DEVICE || memory->memoryKind == CUPTI_ACTIVITY_MEMORY_KIND_MANAGED)
            {
                struct device_usage *device = &devices[memory->deviceId % MAX_DEVICES];
                device->used = 1;
                device->allocated_bytes += memory->memoryOperationType == CUPTI_ACTIVITY_MEMORY_OPERATION_TYPE_RELEASE
                    ? -(long long)memory->bytes
                    : (long long)memory->bytes;

                long long total = 0;
                for (int i = 0; i < MAX_DEVICES; i++)
                {
                    total += devices[i].allocated_bytes > 0 ? devices[i].allocated_bytes : 0;
                }
                if (total > peak_bytes)
                {
                    peak_bytes = total;
                }
            }
        }
        else if (record->kind == CUPTI_ACTIVITY_KIND_MEMCPY)
        {
            CUpti_ActivityMemcpy5 *copy = (CUpti_ActivityMemcpy5 *)record;
            struct device_usage *device = &devices[copy->deviceId % MAX_DEVICES];
            device->used = 1;
            if (copy->copyKind == CUPTI_ACTIVITY_MEMCPY_KIND_HTOD || copy->copyKind == CUPTI_ACTIVITY_MEMCPY_KIND_HTOA)
            {
                device->to_device += copy->bytes;
            }
            else if (copy->copyKind == CUPTI_ACTIVITY_MEMCPY_KIND_DTOH || copy->copyKind == CUPTI_ACTIVITY_MEMCPY_KIND_ATOH)
            {
                device->to_host += copy->bytes;
            }
        }
    }
    pthread_mutex_unlock(&lock);

    size_t dropped = 0;
    if (cuptiActivityGetNumDroppedRecords(context, stream, &dropped) == CUPTI_SUCCESS && dropped)
    {
        fprintf(stderr, "[gpu-meter] dropped %zu activity records\n", dropped);
    }

    free(buffer);
}

static void write_totals(uint64_t timestamp)
{
    int count = 0;
    if (cuDeviceGetCount(&count) != CUDA_SUCCESS || count < 1)
    {
        count = 1;
    }

    static struct device_usage snapshot[MAX_DEVICES];
    static struct kernel_usage kernel_snapshot[MAX_KERNELS];
    pthread_mutex_lock(&lock);
    memcpy(snapshot, devices, sizeof snapshot);
    memcpy(kernel_snapshot, kernels, sizeof kernel_snapshot);
    int kernels_used = kernel_count;
    long long peak = peak_bytes;
    pthread_mutex_unlock(&lock);
    unsigned long long failures = __atomic_load_n(&failed_allocations, __ATOMIC_RELAXED);

    // Write then rename, so the container never reads half a file.
    unsigned long long random;
    if (getrandom(&random, sizeof random, 0) != sizeof random) return;
    char temp_path[80];
    snprintf(temp_path, sizeof temp_path, "%s.%016llx.tmp", path, random);
    int fd = openat(directory_fd, temp_path, O_WRONLY | O_CREAT | O_EXCL | O_NOFOLLOW | O_CLOEXEC, 0644);
    if (fd < 0) return;
    FILE *file = fdopen(fd, "w");
    if (!file)
    {
        close(fd);
        unlinkat(directory_fd, temp_path, 0);
        return;
    }

    fprintf(file, "v4 %d %lld %llu %llu\n", count, peak, (unsigned long long)timestamp, failures);
    for (int i = 0; i < count && i < MAX_DEVICES; i++)
    {
        CUdevice device;
        char bus_id[32];
        if (!snapshot[i].used || cuDeviceGet(&device, i) != CUDA_SUCCESS || cuDeviceGetPCIBusId(bus_id, sizeof bus_id, device) != CUDA_SUCCESS)
        {
            continue;
        }

        fprintf(file, "%s %lld %llu %llu %llu %llu %llu\n",
                bus_id,
                snapshot[i].allocated_bytes < 0 ? 0 : snapshot[i].allocated_bytes,
                snapshot[i].busy_ns,
                snapshot[i].to_device,
                snapshot[i].to_host,
                snapshot[i].launches,
                snapshot[i].kernel_ns);
    }

    for (int listed = 0; listed < LISTED_KERNELS && listed < kernels_used; listed++)
    {
        int best = -1;
        for (int i = 0; i < kernels_used; i++)
        {
            if (kernel_snapshot[i].launches && (best < 0 || kernel_snapshot[i].ns > kernel_snapshot[best].ns)) best = i;
        }
        if (best < 0) break;
        fprintf(file, "k %llu %llu %s\n", kernel_snapshot[best].launches, kernel_snapshot[best].ns, kernel_snapshot[best].name);
        kernel_snapshot[best].launches = 0;
    }

    int failed = ferror(file);
    if (fclose(file) != 0) failed = 1;
    if (!failed) renameat(directory_fd, temp_path, directory_fd, path);
    unlinkat(directory_fd, temp_path, 0);
}

static void *flush_loop(void *unused)
{
    const struct timespec interval = {0, 500 * 1000 * 1000};
    for (;;)
    {
        nanosleep(&interval, NULL);
        // Forced: partly filled buffers are handed over too, so records arrive within the half second.
        uint64_t timestamp = 0;
        cuptiGetTimestamp(&timestamp);
        cuptiActivityFlushAll(CUPTI_ACTIVITY_FLAG_FLUSH_FORCED);
        write_totals(timestamp);
    }

    return NULL;
}

static void remove_file(void)
{
    unlinkat(directory_fd, path, 0);
}

int InitializeInjection(void)
{
    const char *directory = getenv("SOUNDING_GPU_METER_PATH");
    if (!directory || !*directory)
    {
        directory = "/tmp/gpu-meter";
    }

    // Pin the directory: never follow a replacement symlink or change an existing directory's permissions.
    int created = mkdir(directory, 01777) == 0;
    if (!created && errno != EEXIST) return 0;
    directory_fd = open(directory, O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC);
    struct stat directory_stat;
    if (directory_fd < 0) return 0;
    if (fstat(directory_fd, &directory_stat) != 0
        || (directory_stat.st_uid != 0 && directory_stat.st_uid != geteuid())
        || ((directory_stat.st_mode & 0022) && !(directory_stat.st_mode & S_ISVTX))
        || (created && (directory_stat.st_uid != geteuid() || fchmod(directory_fd, 01777) != 0)))
    {
        fprintf(stderr, "[gpu-meter] unsafe reporting directory: %s\n", directory);
        close(directory_fd);
        directory_fd = -1;
        return 0;
    }
    snprintf(path, sizeof path, "%d", (int)getpid());

    CUptiResult result;
    if ((result = cuptiActivityRegisterCallbacks(buffer_requested, buffer_completed)) != CUPTI_SUCCESS)
    {
        report("register callbacks", result);
        return 0;
    }

    if ((result = cuptiActivityEnable(CUPTI_ACTIVITY_KIND_CONCURRENT_KERNEL)) != CUPTI_SUCCESS)
    {
        report("enable kernel activity", result);
    }

    // Without this, kernels launched through CUDA Graphs aren't recorded at all.
    if ((result = cuptiActivityEnable(CUPTI_ACTIVITY_KIND_GRAPH_TRACE)) != CUPTI_SUCCESS)
    {
        report("enable graph activity", result);
    }

    if ((result = cuptiActivityEnable(CUPTI_ACTIVITY_KIND_MEMORY2)) != CUPTI_SUCCESS)
    {
        report("enable memory activity", result);
    }

    if ((result = cuptiActivityEnable(CUPTI_ACTIVITY_KIND_MEMCPY)) != CUPTI_SUCCESS)
    {
        report("enable memcpy activity", result);
    }

    // Failed allocations: only these driver calls are watched, at their return. A process may have one CUPTI
    // subscriber; if the program uses CUPTI itself, this part is skipped.
    if ((result = cuptiSubscribe(&subscriber, (CUpti_CallbackFunc)api_returned, NULL)) == CUPTI_SUCCESS)
    {
        static const CUpti_CallbackId allocations[] = {
            CUPTI_DRIVER_TRACE_CBID_cuMemAlloc_v2, CUPTI_DRIVER_TRACE_CBID_cuMemAllocPitch_v2, CUPTI_DRIVER_TRACE_CBID_cuMemAllocManaged,
            CUPTI_DRIVER_TRACE_CBID_cuMemAllocAsync, CUPTI_DRIVER_TRACE_CBID_cuMemAllocFromPoolAsync, CUPTI_DRIVER_TRACE_CBID_cuMemCreate,
        };
        for (size_t i = 0; i < sizeof allocations / sizeof allocations[0]; i++)
        {
            cuptiEnableCallback(1, subscriber, CUPTI_CB_DOMAIN_DRIVER_API, allocations[i]);
        }
    }
    else
    {
        report("subscribe to allocation calls", result);
    }

    pthread_t thread;
    if (pthread_create(&thread, NULL, flush_loop, NULL) == 0)
    {
        pthread_detach(thread);
    }

    atexit(remove_file);
    return 1;
}
