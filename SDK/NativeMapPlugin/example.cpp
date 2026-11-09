#include "crystal_map_plugin.h"
#include <cstdlib>
#include <cstring>
#include <exception>

// Demonstration container only. XOR is NOT encryption or format protection.
// Real private implementations replace this translation with their own codec.
static const uint8_t magic[] = {'C','S','D','E','M','O','0','1'};
static int32_t fail(cs_map_buffer* error, const char* message) {
    error->size = std::strlen(message);
    error->data = static_cast<uint8_t*>(std::malloc(static_cast<size_t>(error->size)));
    if (error->data) std::memcpy(error->data, message, static_cast<size_t>(error->size));
    else error->size = 0;
    return -1;
}
uint32_t CS_CALL cs_map_api_version() { return 1; }
const char* CS_CALL cs_map_info() {
    return R"({"id":"crystalstudio.example.v1","name":"Native example map","extensions":[".csdemo"],"canRead":true,"canWrite":true})";
}
int32_t CS_CALL cs_map_probe(const uint8_t* input, uint64_t size) {
    return size >= sizeof(magic) && input && std::memcmp(input, magic, sizeof(magic)) == 0 ? 1 : 0;
}
void CS_CALL cs_map_free(cs_map_buffer* buffer) {
    if (buffer) { std::free(buffer->data); *buffer = {}; }
}
int32_t CS_CALL cs_map_read(const uint8_t* input, uint64_t size, cs_map_buffer* output, cs_map_buffer* error) {
    *output = {}; *error = {};
    try {
        if (!cs_map_probe(input, size) || size < sizeof(magic) + 28 || size > 1000000000)
            return fail(error, "Invalid or truncated example map.");
        output->size = size - sizeof(magic);
        output->data = static_cast<uint8_t*>(std::malloc(static_cast<size_t>(output->size)));
        if (!output->data) { output->size = 0; return fail(error, "Out of memory."); }
        for (uint64_t i = 0; i < output->size; ++i) output->data[i] = input[i + sizeof(magic)] ^ 0xa5;
        return 0;
    } catch (...) { cs_map_free(output); return fail(error, "Unexpected read failure."); }
}
int32_t CS_CALL cs_map_write(const uint8_t* input, uint64_t size, cs_map_buffer* output, cs_map_buffer* error) {
    *output = {}; *error = {};
    try {
        if (!input || size < 28 || size > 1000000000 - sizeof(magic) || std::memcmp(input, "CSMP", 4) != 0)
            return fail(error, "Invalid exchange map.");
        output->size = size + sizeof(magic);
        output->data = static_cast<uint8_t*>(std::malloc(static_cast<size_t>(output->size)));
        if (!output->data) { output->size = 0; return fail(error, "Out of memory."); }
        std::memcpy(output->data, magic, sizeof(magic));
        for (uint64_t i = 0; i < size; ++i) output->data[i + sizeof(magic)] = input[i] ^ 0xa5;
        return 0;
    } catch (...) { cs_map_free(output); return fail(error, "Unexpected write failure."); }
}
