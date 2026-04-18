//
//  Use this file to import your target's public headers that you would like to expose to Swift.
//


#ifndef MeloNX-BridgingHeader_h
#define MeloNX-BridgingHeader_h

#include <SDL3/SDL.h>
#include <SDL3/SDL_main.h>

#include <stdint.h>


typedef void (*DataCallbackFn)(const void* data, void* userData);

typedef struct {
    void*   ptr;
    int32_t len;
} CallbackData;

void RegisterCallback(const char* name, DataCallbackFn callback, void* userData);
void UnregisterCallback(const char* name);

uint8_t InvokeCallback(const char* name, const void* data, int32_t len);

#endif
