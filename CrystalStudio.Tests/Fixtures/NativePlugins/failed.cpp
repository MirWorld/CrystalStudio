#define cs_map_info example_info
#define cs_map_write example_write
#include "../../../SDK/NativeMapPlugin/example.cpp"
#undef cs_map_info
#undef cs_map_write
extern "C" CS_EXPORT const char* CS_CALL cs_map_info() {
    return R"({"id":"test.failed","name":"Failed writer test","extensions":[".fail"],"canRead":false,"canWrite":true})";
}
extern "C" CS_EXPORT int32_t CS_CALL cs_map_write(const uint8_t*, uint64_t, cs_map_buffer* output, cs_map_buffer* error) {
    *output = {}; *error = {};
    // Return both buffers on failure to exercise the host's cleanup contract.
    output->data = static_cast<uint8_t*>(std::malloc(1)); output->size = output->data ? 1 : 0;
    return fail(error, "deliberate failure");
}
