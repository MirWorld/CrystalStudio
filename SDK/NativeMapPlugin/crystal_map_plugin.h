#ifndef CRYSTAL_MAP_PLUGIN_H
#define CRYSTAL_MAP_PLUGIN_H
#include <stdint.h>
#include <stddef.h>

#ifdef _WIN32
#define CS_EXPORT __declspec(dllexport)
#define CS_CALL __cdecl
#else
#define CS_EXPORT __attribute__((visibility("default")))
#define CS_CALL
#endif
#ifdef __cplusplus
extern "C" {
#endif

// Default platform alignment (NOT packed). Buffers are always allocated/freed by
// the plugin. Empty = {NULL, 0}. Host copies output before calling cs_map_free.
typedef struct cs_map_buffer { uint8_t* data; uint64_t size; } cs_map_buffer;
CS_EXPORT uint32_t CS_CALL cs_map_api_version(void); // Exactly 1.
// Static, null-terminated UTF-8 JSON. Valid until process exit.
// {"id":"vendor.format.v1","name":"Private map","extensions":[".pmap"],
//  "canRead":true,"canWrite":true}
CS_EXPORT const char* CS_CALL cs_map_info(void);
// Probe: 1 match, 0 no match, negative failure. Inspect magic, not just extension.
CS_EXPORT int32_t CS_CALL cs_map_probe(const uint8_t* input, uint64_t size);
// Transform: 0 success, nonzero failure. Error is UTF-8, no terminating NUL needed.
// Both output and error must be initialized, even on failure. Never throw across
// the ABI. Inputs are borrowed read-only and must not be retained after return.
CS_EXPORT int32_t CS_CALL cs_map_read(const uint8_t* input, uint64_t size, cs_map_buffer* output, cs_map_buffer* error);
CS_EXPORT int32_t CS_CALL cs_map_write(const uint8_t* exchange, uint64_t size, cs_map_buffer* output, cs_map_buffer* error);
CS_EXPORT void CS_CALL cs_map_free(cs_map_buffer* buffer); // Accept zero buffer.

// Wire layout only: little endian, no padding. Do not cast unaligned addresses
// on platforms that require alignment; use memcpy or explicit LE decoding.
#pragma pack(push, 1)
typedef struct cs_map_exchange_header {
    uint8_t magic[4]; // "CSMP"
    int32_t version; // 1
    int32_t width, height;
    int32_t asset_format; // 0 Crystal IDs, 1 Sanxiu editor IDs
    int32_t private_data_size;
    // Followed by private_data_size bytes, int32 cell_count, cell_count records.
} cs_map_exchange_header;
typedef struct cs_map_cell {
    int32_t x, y;
    int16_t back_library; int32_t back_image;
    int16_t middle_library; uint16_t middle_image;
    int16_t front_library, front_image;
    uint8_t front_frames, front_tick, has_front;
    uint8_t door, door_offset, middle_frames, middle_tick;
    int16_t tile_animation, tile_offset;
    uint8_t tile_frames, light;
} cs_map_cell;
#pragma pack(pop)

#ifdef __cplusplus
}
static_assert(sizeof(cs_map_exchange_header) == 24, "Unexpected header layout");
static_assert(sizeof(cs_map_cell) == 35, "Unexpected cell layout");
#endif
#endif
