# sheetnest_core: SheetNest's native geometry core (a shared library called from C# through P/Invoke).
#
#   cmake -S native -B native/build -G "Visual Studio 17 2022" -A x64 && cmake --build native/build --config Release
#   cmake -S native -B native/build && cmake --build native/build          (Linux, gcc or clang)
#
# Results must match the C# code bit for bit, so the floating point is kept strict on every compiler: no
# fast-math and no fused multiply-add contraction.
cmake_minimum_required(VERSION 3.20)

# MSVC: link the C++ runtime statically but the C runtime (ucrtbase.dll, part of Windows) dynamically. The
# app then needs no Visual C++ redistributable, and sin/cos/acos come from the same ucrtbase.dll .NET calls.
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded")

project(sheetnest_core LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)
if(NOT CMAKE_BUILD_TYPE AND NOT CMAKE_CONFIGURATION_TYPES)
  set(CMAKE_BUILD_TYPE Release)
endif()

add_library(sheetnest_core SHARED
  sheetnest_core/src/compact.cpp
  sheetnest_core/src/collision.cpp
  third_party/clipper/clipper.cpp)

target_include_directories(sheetnest_core
  PUBLIC sheetnest_core/include
  PRIVATE third_party/clipper)
target_compile_definitions(sheetnest_core PRIVATE SHEETNEST_CORE_BUILD)
set_target_properties(sheetnest_core PROPERTIES
  CXX_VISIBILITY_PRESET hidden
  VISIBILITY_INLINES_HIDDEN ON
  PREFIX "")

if(MSVC)
  # /fp:precise does not contract a*b+c into an FMA on VS 2022 (contraction is the separate /fp:contract).
  target_compile_options(sheetnest_core PRIVATE /W3 /EHsc /fp:precise)
  target_link_options(sheetnest_core PRIVATE /NODEFAULTLIB:libucrt.lib ucrt.lib)
else()
  target_compile_options(sheetnest_core PRIVATE -O2 -fno-fast-math -ffp-contract=off)
endif()
