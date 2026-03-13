#pragma once

#include <stddef.h>
#include <stdint.h>

#ifdef _WIN32
#define LC_API __declspec(dllexport)
#else
#define LC_API __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif

    // Opaque handles
    typedef struct lc_manager_t   lc_manager_t;
    typedef struct lc_camera_t    lc_camera_t;
    typedef struct lc_config_t    lc_config_t;
    typedef struct lc_allocator_t lc_allocator_t;
    typedef struct lc_request_t   lc_request_t;

    // Callback fired on the libcamera thread when a frame is ready
    typedef void (*lc_request_completed_cb)(lc_request_t* request, void* user_data);

    // Frame metadata — populated by lc_request_get_frame_info()
    typedef struct {
        uint32_t width;
        uint32_t height;
        uint32_t stride;        // bytes per row — NOT width * bpp
        uint32_t pixel_format;  // FourCC e.g. 0x56595559 = YUYV
        uint64_t timestamp_us;  // microseconds, monotonic
        uint32_t sequence;      // frame counter since camera start
    } lc_frame_info_t;

    // --- Manager ---
    LC_API lc_manager_t* lc_manager_create(void);
    LC_API void          lc_manager_destroy(lc_manager_t* manager);
    LC_API int           lc_manager_start(lc_manager_t* manager);
    LC_API int           lc_manager_stop(lc_manager_t* manager);
    LC_API int           lc_manager_camera_count(lc_manager_t* manager);
    LC_API int           lc_manager_get_camera_id(lc_manager_t* manager, int index, char* buffer, size_t buffer_size);

    // --- Camera lifecycle ---
    LC_API lc_camera_t* lc_camera_open(lc_manager_t* manager, const char* camera_id);
    LC_API void         lc_camera_close(lc_camera_t* camera);
    LC_API int          lc_camera_acquire(lc_camera_t* camera);
    LC_API int          lc_camera_release(lc_camera_t* camera);
    LC_API int          lc_camera_start(lc_camera_t* camera);
    LC_API int          lc_camera_stop(lc_camera_t* camera);
    LC_API int          lc_camera_queue_request(lc_camera_t* camera, lc_request_t* request);
    LC_API int          lc_camera_set_request_completed_callback(
        lc_camera_t* camera,
        lc_request_completed_cb callback,
        void* user_data);

    // --- Camera capabilities / properties ---
    LC_API int lc_camera_get_model(lc_camera_t* camera, char* buffer, size_t buffer_size);
    LC_API int lc_camera_get_pixel_array_size(
        lc_camera_t* camera,
        uint32_t* width,
        uint32_t* height);

    LC_API int lc_camera_get_supported_pixel_format_count(lc_camera_t* camera);
    LC_API int lc_camera_get_supported_pixel_format(
        lc_camera_t* camera,
        int index,
        uint32_t* fourcc);

    LC_API int lc_camera_get_supported_size_count(
        lc_camera_t* camera,
        uint32_t fourcc);

    LC_API int lc_camera_get_supported_size(
        lc_camera_t* camera,
        uint32_t fourcc,
        int index,
        uint32_t* width,
        uint32_t* height);

    LC_API int lc_camera_get_format_size_range(
        lc_camera_t* camera,
        uint32_t fourcc,
        uint32_t* min_width,
        uint32_t* min_height,
        uint32_t* max_width,
        uint32_t* max_height);

    // --- Configuration ---
    LC_API lc_config_t* lc_camera_generate_configuration(lc_camera_t* camera);
    LC_API void         lc_config_destroy(lc_config_t* config);
    LC_API int          lc_config_set_size(lc_config_t* config, uint32_t width, uint32_t height);
    LC_API int          lc_config_set_pixel_format(lc_config_t* config, uint32_t fourcc);
    LC_API int          lc_config_validate(lc_config_t* config);
    LC_API int          lc_camera_configure(lc_camera_t* camera, lc_config_t* config);
    LC_API int          lc_config_get_stride(lc_config_t* config, uint32_t* stride);
    LC_API int          lc_config_get_frame_size(lc_config_t* config, uint32_t* frame_size);

    // --- Buffer allocator ---
    LC_API lc_allocator_t* lc_allocator_create(lc_camera_t* camera);
    LC_API void            lc_allocator_destroy(lc_allocator_t* allocator);
    LC_API int             lc_allocator_allocate(lc_allocator_t* allocator);
    LC_API int             lc_allocator_buffer_count(lc_allocator_t* allocator);
    LC_API int             lc_allocator_get_buffer(lc_allocator_t* allocator, int index, void** buffer_handle);
    LC_API int             lc_allocator_get_buffer_plane_info(
        lc_allocator_t* allocator,
        void* buffer_handle,
        int plane_index,
        void** data,
        size_t* length);

    // --- Request ---
    LC_API lc_request_t* lc_request_create(lc_camera_t* camera);
    LC_API void          lc_request_destroy(lc_request_t* request);
    LC_API int           lc_request_attach_buffer(lc_request_t* request, void* buffer_handle);
    LC_API int           lc_request_reuse(lc_request_t* request);
    LC_API int           lc_request_get_frame_info(lc_request_t* request, lc_frame_info_t* info);
    LC_API int           lc_request_get_plane_data(
        lc_request_t* request,
        lc_allocator_t* allocator,
        int plane_index,
        void** data,
        size_t* length);

    // --- Controls (set on request before queuing — libcamera 0.7 pattern) ---
    LC_API int lc_request_set_control_exposure(lc_request_t* request, int32_t exposure_us);
    LC_API int lc_request_set_control_gain(lc_request_t* request, float gain);
    LC_API int lc_request_set_control_brightness(lc_request_t* request, float brightness);
    LC_API int lc_request_set_control_contrast(lc_request_t* request, float contrast);
    LC_API int lc_request_set_control_awb(lc_request_t* request, int enable);
    LC_API int lc_request_set_control_ae(lc_request_t* request, int enable);

#ifdef __cplusplus
}
#endif