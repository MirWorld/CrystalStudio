#define cs_map_info example_info
#include "../../../SDK/NativeMapPlugin/example.cpp"
#undef cs_map_info
extern "C" CS_EXPORT const char* CS_CALL cs_map_info() {
    return R"({"id":"test.readonly","name":"Read only test","extensions":[".csdemo"],"canRead":true,"canWrite":false})";
}
