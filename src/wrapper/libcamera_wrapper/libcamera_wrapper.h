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
    /* The sensor mode to take the picture from, by its output size and bit depth (1640x1232 at 10 bits on the IMX219
       is its whole view, binned). Without it libcamera picks the mode nearest the picture's size, which for a small
       picture can be a crop of the middle of the sensor. */
    LC_API int          lc_config_set_sensor_mode(lc_config_t* config, uint32_t width, uint32_t height, uint32_t bit_depth);
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

    // --- Any control, by its libcamera name ("AnalogueGain", "AfMode", "ScalerCrop") ---
    // Values go in and out as doubles: one for a number, a flag (0 or 1) or a choice, two for a size or a pair
    // (ColourGains, FrameDurationLimits), four for a rectangle (x, y, width, height). Each returns how many values
    // it wrote, or a negative number: -2 no such control, -3 the camera has none of it, -4 the wrong number of
    // values, -5 a type this cannot carry.

    // The controls the camera takes, by index, and each one's type: 1 bool, 2 byte, 3 int32, 4 int64, 5 float,
    // 7 rectangle, 8 size, as libcamera numbers them; is_array is 1 for one that carries several values.
    LC_API int lc_camera_control_count(lc_camera_t* camera);
    LC_API int lc_camera_control_name(lc_camera_t* camera, int index, char* buffer, size_t buffer_size);
    LC_API int lc_control_type(const char* name, int* type, int* is_array);
    // which: 0 the least, 1 the most, 2 what it starts at.
    LC_API int lc_camera_control_limit(lc_camera_t* camera, const char* name, int which, double* values, int capacity);
    // The values the camera allows, for a control that lists them (a choice it supports only some of).
    LC_API int lc_camera_control_value_count(lc_camera_t* camera, const char* name);
    LC_API int lc_camera_control_value(lc_camera_t* camera, const char* name, int index, double* value);
    // The names of a choice's values, as libcamera has them: AfMode's 0 is "AfModeManual".
    LC_API int lc_control_enum_count(const char* name);
    LC_API int lc_control_enum(const char* name, int index, int32_t* value, char* buffer, size_t buffer_size);

    LC_API int lc_request_set_control(lc_request_t* request, const char* name, const double* values, int count);
    // What a finished frame was taken with: its exposure, gain, colour temperature, lens position.
    LC_API int lc_request_get_metadata(lc_request_t* request, const char* name, double* values, int capacity);

    // Mirrors the picture across (hflip) and turns it upside down (vflip); both is half a turn.
    LC_API int lc_config_set_orientation(lc_config_t* config, int hflip, int vflip);

#ifdef __cplusplus
}
#endif