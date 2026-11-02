cmake_minimum_required(VERSION 3.22)
project(kissik_whisper C CXX)

set(BUILD_SHARED_LIBS OFF CACHE BOOL "" FORCE)
set(WHISPER_BUILD_TESTS OFF CACHE BOOL "" FORCE)
set(WHISPER_BUILD_EXAMPLES OFF CACHE BOOL "" FORCE)
set(WHISPER_BUILD_SERVER OFF CACHE BOOL "" FORCE)
set(GGML_OPENMP OFF CACHE BOOL "" FORCE)
set(GGML_NATIVE OFF CACHE BOOL "" FORCE)
# WHISPER_SRC is the release source unpacked by the Gradle task fetchWhisper.
add_subdirectory("${WHISPER_SRC}" whisper)

add_library(kissik_whisper SHARED jni.cpp)
target_link_libraries(kissik_whisper whisper log)
# 16 KB page-size devices (Android 15+) need 16 KB-aligned LOAD segments.
target_link_options(kissik_whisper PRIVATE "-Wl,-z,max-page-size=16384")
