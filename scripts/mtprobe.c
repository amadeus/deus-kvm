// Probe: does the private MultitouchSupport framework still list trackpads and deliver contact frames?
// Build and run: clang -O1 -o /tmp/mtprobe scripts/mtprobe.c -framework CoreFoundation && /tmp/mtprobe 10
// Verified 2026-11-05 on macOS 26.2 with a Bluetooth Magic Trackpad: frames every 15 ms while touching.
#include <CoreFoundation/CoreFoundation.h>
#include <dlfcn.h>
#include <stdio.h>
#include <stdint.h>
#include <unistd.h>

typedef struct { float x, y; } MTPoint;
typedef struct { MTPoint pos, vel; } MTVector;
typedef struct {
    int32_t frame; double timestamp; int32_t identifier; int32_t state; int32_t fingerID; int32_t handID;
    MTVector normalized; float size; int32_t zero1; float angle, majorAxis, minorAxis;
    MTVector absolute; int32_t zero2[2]; float density;
} MTTouch;
typedef void *MTDeviceRef;
typedef int (*MTContactCallback)(MTDeviceRef, MTTouch *, int, double, int);

static int frames = 0, printed = 0;
static int cb(MTDeviceRef dev, MTTouch *t, int n, double ts, int frame) {
    frames++;
    if (printed < 12 && n > 0) {
        printed++;
        printf("frame %d t=%.4f contacts=%d:", frame, ts, n);
        for (int i = 0; i < n && i < 5; i++)
            printf(" [id=%d st=%d x=%.3f y=%.3f sz=%.2f]", t[i].identifier, t[i].state, t[i].normalized.pos.x, t[i].normalized.pos.y, t[i].size);
        printf("\n"); fflush(stdout);
    }
    return 0;
}

int main(int argc, char **argv) {
    int seconds = argc > 1 ? atoi(argv[1]) : 6;
    void *h = dlopen("/System/Library/PrivateFrameworks/MultitouchSupport.framework/MultitouchSupport", RTLD_NOW);
    if (!h) { printf("dlopen failed: %s\n", dlerror()); return 1; }
    CFArrayRef (*createList)(void) = dlsym(h, "MTDeviceCreateList");
    void (*reg)(MTDeviceRef, MTContactCallback) = dlsym(h, "MTRegisterContactFrameCallback");
    int (*start)(MTDeviceRef, int) = dlsym(h, "MTDeviceStart");
    void (*stop)(MTDeviceRef) = dlsym(h, "MTDeviceStop");
    int (*isBuiltIn)(MTDeviceRef) = dlsym(h, "MTDeviceIsBuiltIn");
    int (*family)(MTDeviceRef, int *) = dlsym(h, "MTDeviceGetFamilyID");
    int (*dims)(MTDeviceRef, int *, int *) = dlsym(h, "MTDeviceGetSensorSurfaceDimensions");
    int (*running)(MTDeviceRef) = dlsym(h, "MTDeviceIsRunning");
    printf("symbols: list=%p reg=%p start=%p stop=%p builtIn=%p family=%p dims=%p running=%p\n",
           (void*)createList, (void*)reg, (void*)start, (void*)stop, (void*)isBuiltIn, (void*)family, (void*)dims, (void*)running);
    if (!createList || !reg || !start) return 2;
    CFArrayRef list = createList();
    CFIndex count = list ? CFArrayGetCount(list) : 0;
    printf("devices: %ld\n", (long)count);
    for (CFIndex i = 0; i < count; i++) {
        MTDeviceRef d = (MTDeviceRef)CFArrayGetValueAtIndex(list, i);
        int fam = -1, w = -1, hh = -1;
        if (family) family(d, &fam);
        if (dims) dims(d, &w, &hh);
        printf("  device %ld: builtIn=%d family=%d surface=%dx%d (units of 0.01 mm)\n", (long)i, isBuiltIn ? isBuiltIn(d) : -1, fam, w, hh);
        reg(d, cb);
        int rc = start(d, 0);
        printf("  MTDeviceStart -> %d running=%d\n", rc, running ? running(d) : -1);
    }
    printf("listening %d s: touch/move on the trackpad now...\n", seconds); fflush(stdout);
    CFRunLoopRunInMode(kCFRunLoopDefaultMode, seconds, false);
    for (CFIndex i = 0; i < count; i++) if (stop) stop((MTDeviceRef)CFArrayGetValueAtIndex(list, i));
    printf("done: %d contact frames received (%d printed)\n", frames, printed);
    return 0;
}
