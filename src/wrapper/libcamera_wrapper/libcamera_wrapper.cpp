#include "libcamera_wrapper.h"

#include <libcamera/camera.h>
#include <libcamera/camera_manager.h>
#include <libcamera/control_ids.h>
#include <libcamera/controls.h>
#include <libcamera/framebuffer.h>
#include <libcamera/framebuffer_allocator.h>
#include <libcamera/orientation.h>
#include <libcamera/property_ids.h>
#include <libcamera/request.h>
#include <libcamera/stream.h>

#include <sys/mman.h>
#include <unistd.h>

#include <cmath>
#include <cstring>
#include <limits>
#include <memory>
#include <span>
#include <string>
#include <unordered_map>
#include <vector>

using namespace libcamera;

// ===== INTERNAL STRUCTURES =====

struct MappedPlane {
    void* addr = nullptr;
    size_t length = 0;
    off_t offset = 0;
};

struct lc_manager_t {
    std::unique_ptr<CameraManager> manager;
    bool started = false;
};

struct lc_camera_t {
    lc_manager_t* owner = nullptr;
    std::shared_ptr<Camera> camera;
    Stream* stream = nullptr;
    bool acquired = false;
    bool started = false;
    lc_request_completed_cb callback = nullptr;
    void* callback_user = nullptr;
};

struct lc_config_t {
    std::unique_ptr<CameraConfiguration> config;
    Stream* stream = nullptr;
};

struct lc_allocator_t {
    lc_camera_t* camera = nullptr;
    std::unique_ptr<FrameBufferAllocator> allocator;
    Stream* stream = nullptr;
    std::vector<FrameBuffer*> buffers;
    std::unordered_map<const FrameBuffer*, std::vector<MappedPlane>> mapped;
};

struct lc_request_t {
    lc_camera_t* camera = nullptr;
    std::unique_ptr<Request> request;
    FrameBuffer* buffer = nullptr;
};

// ===== INTERNAL HELPERS =====

namespace {

    static int safe_copy_string(const std::string& src, char* dst, size_t dst_size)
    {
        if (!dst || dst_size == 0) return -1;
        size_t n = src.size();
        if (n >= dst_size) n = dst_size - 1;
        std::memcpy(dst, src.c_str(), n);
        dst[n] = '\0';
        return 0;
    }

    static void unmap_buffers(lc_allocator_t* a)
    {
        if (!a) return;
        for (auto& kv : a->mapped)
            for (auto& p : kv.second)
                if (p.addr && p.length > 0)
                    munmap(p.addr, p.length);
        a->mapped.clear();
    }

    static int map_buffers(lc_allocator_t* a)
    {
        if (!a) return -1;
        a->mapped.clear();

        for (FrameBuffer* buf : a->buffers) {
            std::vector<MappedPlane> planes;

            for (const FrameBuffer::Plane& plane : buf->planes()) {
                int fd = plane.fd.get();
                if (fd < 0) return -2;

                size_t len = plane.offset + plane.length;
                void* addr = mmap(nullptr, len, PROT_READ | PROT_WRITE, MAP_SHARED, fd, 0);
                if (addr == MAP_FAILED) return -3;

                planes.push_back({ addr, len, static_cast<off_t>(plane.offset) });
            }

            a->mapped[buf] = std::move(planes);
        }

        return 0;
    }

    static int get_stream_formats(lc_camera_t* c, StreamFormats* outFormats)
    {
        if (!c || !outFormats) return -1;

        std::vector<StreamRole> roles = { StreamRole::Viewfinder };
        auto cfg = c->camera->generateConfiguration(roles);
        if (!cfg || cfg->empty()) return -2;

        *outFormats = cfg->at(0).formats();
        return 0;
    }

    // Every control libcamera knows, by name, made once: the camera's own list only has what it takes, and what a
    // frame reports (ColourTemperature, Lux) is not among those.
    static const ControlId* control_named(const char* name)
    {
        static const std::unordered_map<std::string, const ControlId*> byName = [] {
            std::unordered_map<std::string, const ControlId*> map;
            for (const auto& kv : controls::controls)
                map[kv.second->name()] = kv.second;
            return map;
        }();
        if (!name) return nullptr;
        auto it = byName.find(name);
        return it == byName.end() ? nullptr : it->second;
    }

    template <typename T>
    static int put_numbers(const ControlValue& v, double* out, int capacity)
    {
        if (v.isArray()) {
            auto span = v.get<std::span<const T>>();
            int n = static_cast<int>(span.size());
            if (n > capacity) return -4;
            for (int i = 0; i < n; i++) out[i] = static_cast<double>(span[i]);
            return n;
        }
        if (capacity < 1) return -4;
        out[0] = static_cast<double>(v.get<T>());
        return 1;
    }

    // A control's value as doubles: one for a number, two for a size, four for a rectangle, several for an array.
    static int to_doubles(const ControlValue& v, double* out, int capacity)
    {
        if (!out) return -1;
        switch (v.type()) {
        case ControlTypeNone: return 0;
        case ControlTypeBool:
            if (v.isArray()) return -5;
            if (capacity < 1) return -4;
            out[0] = v.get<bool>() ? 1.0 : 0.0;
            return 1;
        case ControlTypeByte: return put_numbers<uint8_t>(v, out, capacity);
        case ControlTypeInteger32: return put_numbers<int32_t>(v, out, capacity);
        case ControlTypeInteger64: return put_numbers<int64_t>(v, out, capacity);
        case ControlTypeFloat: return put_numbers<float>(v, out, capacity);
        case ControlTypeRectangle: {
            if (v.isArray() || capacity < 4) return v.isArray() ? -5 : -4;
            auto r = v.get<Rectangle>();
            out[0] = r.x; out[1] = r.y; out[2] = r.width; out[3] = r.height;
            return 4;
        }
        case ControlTypeSize: {
            if (v.isArray() || capacity < 2) return v.isArray() ? -5 : -4;
            auto z = v.get<Size>();
            out[0] = z.width; out[1] = z.height;
            return 2;
        }
        default: return -5;
        }
    }

    template <typename T>
    static ControlValue numbers_as(const ControlId* id, const double* values, int count)
    {
        if (id->isArray()) {
            std::vector<T> items(count);
            for (int i = 0; i < count; i++) items[i] = static_cast<T>(std::llround(values[i]));
            return ControlValue(std::span<const T>(items.data(), items.size()));
        }
        return ControlValue(static_cast<T>(std::llround(values[0])));
    }

    // Doubles as a value of the control's own type, or a ControlValue of type None when they cannot be.
    static ControlValue from_doubles(const ControlId* id, const double* values, int count)
    {
        if (!values || count < 1) return ControlValue();
        if (id->isArray() && id->size() > 0 && static_cast<size_t>(count) != id->size()) return ControlValue();
        if (!id->isArray() && id->type() != ControlTypeRectangle && id->type() != ControlTypeSize && count != 1) return ControlValue();
        switch (id->type()) {
        case ControlTypeBool: return id->isArray() ? ControlValue() : ControlValue(values[0] != 0.0);
        case ControlTypeByte: return numbers_as<uint8_t>(id, values, count);
        case ControlTypeInteger32: return numbers_as<int32_t>(id, values, count);
        case ControlTypeInteger64: return numbers_as<int64_t>(id, values, count);
        case ControlTypeFloat: {
            if (id->isArray()) {
                std::vector<float> items(values, values + count);
                return ControlValue(std::span<const float>(items.data(), items.size()));
            }
            return ControlValue(static_cast<float>(values[0]));
        }
        case ControlTypeRectangle:
            if (id->isArray() || count != 4) return ControlValue();
            return ControlValue(Rectangle(static_cast<int>(std::lround(values[0])), static_cast<int>(std::lround(values[1])),
                static_cast<unsigned int>(std::lround(values[2])), static_cast<unsigned int>(std::lround(values[3]))));
        case ControlTypeSize:
            if (id->isArray() || count != 2) return ControlValue();
            return ControlValue(Size(static_cast<unsigned int>(std::lround(values[0])), static_cast<unsigned int>(std::lround(values[1]))));
        default: return ControlValue();
        }
    }

    // The camera's own entry for a control, or null when it does not take it.
    static const ControlInfo* camera_info(lc_camera_t* c, const char* name, const ControlId** idOut = nullptr)
    {
        if (!c) return nullptr;
        const ControlId* id = control_named(name);
        if (!id) return nullptr;
        if (idOut) *idOut = id;
        const ControlInfoMap& infos = c->camera->controls();
        auto it = infos.find(id);
        return it == infos.end() ? nullptr : &it->second;
    }

} // namespace

// ===== PUBLIC C API =====

extern "C" {

    // --- Manager ---

    lc_manager_t* lc_manager_create()
    {
        auto* m = new lc_manager_t();
        m->manager = std::make_unique<CameraManager>();
        return m;
    }

    void lc_manager_destroy(lc_manager_t* m)
    {
        if (!m) return;
        if (m->started) m->manager->stop();
        delete m;
    }

    int lc_manager_start(lc_manager_t* m)
    {
        if (!m) return -1;
        if (m->started) return 0;

        int ret = m->manager->start();
        if (ret < 0) return ret;

        m->started = true;
        return 0;
    }

    int lc_manager_stop(lc_manager_t* m)
    {
        if (!m) return -1;

        if (m->started) {
            m->manager->stop();
            m->started = false;
        }

        return 0;
    }

    int lc_manager_camera_count(lc_manager_t* m)
    {
        if (!m || !m->started) return -1;
        return static_cast<int>(m->manager->cameras().size());
    }

    int lc_manager_get_camera_id(lc_manager_t* m, int index, char* buf, size_t buf_size)
    {
        if (!m || !m->started || !buf) return -1;

        const auto& cams = m->manager->cameras();
        if (index < 0 || static_cast<size_t>(index) >= cams.size()) return -2;

        return safe_copy_string(cams[index]->id(), buf, buf_size);
    }

    // --- Camera ---

    lc_camera_t* lc_camera_open(lc_manager_t* m, const char* camera_id)
    {
        if (!m || !m->started || !camera_id) return nullptr;

        std::shared_ptr<Camera> cam = m->manager->get(camera_id);
        if (!cam) return nullptr;

        auto* c = new lc_camera_t();
        c->owner = m;
        c->camera = cam;
        return c;
    }

    void lc_camera_close(lc_camera_t* c)
    {
        if (!c) return;

        c->camera->requestCompleted.disconnect();

        if (c->started)
            c->camera->stop();

        if (c->acquired)
            c->camera->release();

        delete c;
    }

    int lc_camera_acquire(lc_camera_t* c)
    {
        if (!c) return -1;
        if (c->acquired) return 0;

        int ret = c->camera->acquire();
        if (ret < 0) return ret;

        c->acquired = true;
        return 0;
    }

    int lc_camera_release(lc_camera_t* c)
    {
        if (!c) return -1;
        if (!c->acquired) return 0;

        c->camera->release();
        c->acquired = false;
        return 0;
    }

    int lc_camera_start(lc_camera_t* c)
    {
        if (!c) return -1;
        if (c->started) return 0;

        int ret = c->camera->start();
        if (ret < 0) return ret;

        c->started = true;
        return 0;
    }

    int lc_camera_stop(lc_camera_t* c)
    {
        if (!c) return -1;
        if (!c->started) return 0;

        int ret = c->camera->stop();
        if (ret < 0) return ret;

        c->started = false;
        return 0;
    }

    int lc_camera_queue_request(lc_camera_t* c, lc_request_t* r)
    {
        if (!c || !r || !r->request) return -1;
        return c->camera->queueRequest(r->request.get());
    }

    static lc_camera_t* g_callback_camera = nullptr;

    static void requestCompletedHandlerCtx(Request* request)
    {
        lc_camera_t* c = g_callback_camera;
        if (!c || !c->callback) return;
        if (request->status() == Request::RequestCancelled) return;

        const Request::BufferMap& bufs = request->buffers();
        if (bufs.empty()) return;

        FrameBuffer* fb = bufs.begin()->second;
        auto* wrapper = reinterpret_cast<lc_request_t*>(fb->cookie());
        if (wrapper)
            c->callback(wrapper, c->callback_user);
    }

    int lc_camera_set_request_completed_callback(
        lc_camera_t* c,
        lc_request_completed_cb callback,
        void* user_data)
    {
        if (!c) return -1;

        c->callback = callback;
        c->callback_user = user_data;
        g_callback_camera = c;

        c->camera->requestCompleted.disconnect();
        c->camera->requestCompleted.connect(requestCompletedHandlerCtx);

        return 0;
    }

    // --- Camera capabilities / properties ---

    int lc_camera_get_model(lc_camera_t* c, char* buffer, size_t buffer_size)
    {
        if (!c || !buffer || buffer_size == 0) return -1;

        const ControlList& props = c->camera->properties();
        auto model = props.get(properties::Model);
        if (!model) return -2;

        std::string modelStr(*model);
        return safe_copy_string(modelStr, buffer, buffer_size);
    }

    int lc_camera_get_pixel_array_size(
        lc_camera_t* c,
        uint32_t* width,
        uint32_t* height)
    {
        if (!c || !width || !height) return -1;

        // The whole pixel array's area: Raspberry Pi's libcamera builds since October 2026 have no PixelArraySize, and
        // the area has the same width and height in every build.
        const ControlList& props = c->camera->properties();
        auto size = props.get(properties::PixelArrayArea);
        if (!size) return -2;

        *width = size->width;
        *height = size->height;
        return 0;
    }

    int lc_camera_get_supported_pixel_format_count(lc_camera_t* c)
    {
        if (!c) return -1;

        StreamFormats formats;
        int ret = get_stream_formats(c, &formats);
        if (ret < 0) return ret;

        return static_cast<int>(formats.pixelformats().size());
    }

    int lc_camera_get_supported_pixel_format(
        lc_camera_t* c,
        int index,
        uint32_t* fourcc)
    {
        if (!c || !fourcc) return -1;

        StreamFormats formats;
        int ret = get_stream_formats(c, &formats);
        if (ret < 0) return ret;

        const auto& pixelFormats = formats.pixelformats();
        if (index < 0 || static_cast<size_t>(index) >= pixelFormats.size()) return -3;

        *fourcc = pixelFormats[index].fourcc();
        return 0;
    }

    int lc_camera_get_supported_size_count(
        lc_camera_t* c,
        uint32_t fourcc)
    {
        if (!c) return -1;

        StreamFormats formats;
        int ret = get_stream_formats(c, &formats);
        if (ret < 0) return ret;

        PixelFormat fmt(fourcc);
        const auto sizes = formats.sizes(fmt);
        return static_cast<int>(sizes.size());
    }

    int lc_camera_get_supported_size(
        lc_camera_t* c,
        uint32_t fourcc,
        int index,
        uint32_t* width,
        uint32_t* height)
    {
        if (!c || !width || !height) return -1;

        StreamFormats formats;
        int ret = get_stream_formats(c, &formats);
        if (ret < 0) return ret;

        PixelFormat fmt(fourcc);
        const auto sizes = formats.sizes(fmt);
        if (index < 0 || static_cast<size_t>(index) >= sizes.size()) return -3;

        *width = sizes[index].width;
        *height = sizes[index].height;
        return 0;
    }

    int lc_camera_get_format_size_range(
        lc_camera_t* c,
        uint32_t fourcc,
        uint32_t* min_width,
        uint32_t* min_height,
        uint32_t* max_width,
        uint32_t* max_height)
    {
        if (!c || !min_width || !min_height || !max_width || !max_height) return -1;

        StreamFormats formats;
        int ret = get_stream_formats(c, &formats);
        if (ret < 0) return ret;

        PixelFormat fmt(fourcc);
        const auto sizes = formats.sizes(fmt);
        if (sizes.empty()) return -3;

        uint32_t minW = std::numeric_limits<uint32_t>::max();
        uint32_t minH = std::numeric_limits<uint32_t>::max();
        uint32_t maxW = 0;
        uint32_t maxH = 0;

        for (const auto& s : sizes) {
            if (s.width < minW) minW = s.width;
            if (s.height < minH) minH = s.height;
            if (s.width > maxW) maxW = s.width;
            if (s.height > maxH) maxH = s.height;
        }

        *min_width = minW;
        *min_height = minH;
        *max_width = maxW;
        *max_height = maxH;
        return 0;
    }

    // Controls in libcamera 0.7 are set via Request::controls()
    // lc_request_set_control_* functions below set controls on a request
    // before queuing it. This is the correct 0.7 pattern.

    int lc_request_set_control_exposure(lc_request_t* r, int32_t exposure_us)
    {
        if (!r || !r->request) return -1;
        r->request->controls().set(controls::ExposureTime, exposure_us);
        return 0;
    }

    int lc_request_set_control_gain(lc_request_t* r, float gain)
    {
        if (!r || !r->request) return -1;
        r->request->controls().set(controls::AnalogueGain, gain);
        return 0;
    }

    int lc_request_set_control_brightness(lc_request_t* r, float brightness)
    {
        if (!r || !r->request) return -1;
        r->request->controls().set(controls::Brightness, brightness);
        return 0;
    }

    int lc_request_set_control_contrast(lc_request_t* r, float contrast)
    {
        if (!r || !r->request) return -1;
        r->request->controls().set(controls::Contrast, contrast);
        return 0;
    }

    int lc_request_set_control_awb(lc_request_t* r, int enable)
    {
        if (!r || !r->request) return -1;
        r->request->controls().set(controls::AwbEnable, static_cast<bool>(enable));
        return 0;
    }

    int lc_request_set_control_ae(lc_request_t* r, int enable)
    {
        if (!r || !r->request) return -1;
        r->request->controls().set(controls::AeEnable, static_cast<bool>(enable));
        return 0;
    }

    // --- Any control, by name ---

    int lc_camera_control_count(lc_camera_t* c)
    {
        if (!c) return -1;
        return static_cast<int>(c->camera->controls().size());
    }

    int lc_camera_control_name(lc_camera_t* c, int index, char* buffer, size_t buffer_size)
    {
        if (!c || !buffer) return -1;
        const ControlInfoMap& infos = c->camera->controls();
        if (index < 0 || static_cast<size_t>(index) >= infos.size()) return -2;
        auto it = infos.begin();
        std::advance(it, index);
        return safe_copy_string(it->first->name(), buffer, buffer_size);
    }

    int lc_control_type(const char* name, int* type, int* is_array)
    {
        if (!type || !is_array) return -1;
        const ControlId* id = control_named(name);
        if (!id) return -2;
        *type = static_cast<int>(id->type());
        *is_array = id->isArray() ? 1 : 0;
        return 0;
    }

    int lc_camera_control_limit(lc_camera_t* c, const char* name, int which, double* values, int capacity)
    {
        if (!c || !values) return -1;
        const ControlInfo* info = camera_info(c, name);
        if (!info) return control_named(name) ? -3 : -2;
        const ControlValue& v = which == 0 ? info->min() : which == 1 ? info->max() : info->def();
        return to_doubles(v, values, capacity);
    }

    int lc_camera_control_value_count(lc_camera_t* c, const char* name)
    {
        const ControlInfo* info = camera_info(c, name);
        if (!info) return control_named(name) ? -3 : -2;
        return static_cast<int>(info->values().size());
    }

    int lc_camera_control_value(lc_camera_t* c, const char* name, int index, double* value)
    {
        if (!value) return -1;
        const ControlInfo* info = camera_info(c, name);
        if (!info) return control_named(name) ? -3 : -2;
        const auto& values = info->values();
        if (index < 0 || static_cast<size_t>(index) >= values.size()) return -4;
        return to_doubles(values[index], value, 1);
    }

    int lc_control_enum_count(const char* name)
    {
        const ControlId* id = control_named(name);
        if (!id) return -2;
        return static_cast<int>(id->enumerators().size());
    }

    int lc_control_enum(const char* name, int index, int32_t* value, char* buffer, size_t buffer_size)
    {
        if (!value || !buffer) return -1;
        const ControlId* id = control_named(name);
        if (!id) return -2;
        const auto& names = id->enumerators();
        if (index < 0 || static_cast<size_t>(index) >= names.size()) return -4;
        auto it = names.begin();
        std::advance(it, index);
        *value = it->first;
        return safe_copy_string(it->second, buffer, buffer_size);
    }

    int lc_request_set_control(lc_request_t* r, const char* name, const double* values, int count)
    {
        if (!r || !r->request || !r->camera) return -1;
        const ControlId* id = nullptr;
        if (!camera_info(r->camera, name, &id)) return id || control_named(name) ? -3 : -2;
        ControlValue v = from_doubles(id, values, count);
        if (v.type() == ControlTypeNone) return -4;
        r->request->controls().set(id->id(), v);
        return count;
    }

    int lc_request_get_metadata(lc_request_t* r, const char* name, double* values, int capacity)
    {
        if (!r || !r->request) return -1;
        const ControlId* id = control_named(name);
        if (!id) return -2;
        const ControlList& meta = r->request->metadata();
        if (!meta.contains(id->id())) return -3;
        return to_doubles(meta.get(id->id()), values, capacity);
    }

    int lc_config_set_orientation(lc_config_t* cfg, int hflip, int vflip)
    {
        if (!cfg || !cfg->config) return -1;
        // A vertical flip alone is half a turn and a mirror; both flips together are the half turn alone.
        cfg->config->orientation = hflip && vflip ? Orientation::Rotate180
            : vflip ? Orientation::Rotate180Mirror
            : hflip ? Orientation::Rotate0Mirror
            : Orientation::Rotate0;
        return 0;
    }

    // --- Config ---

    lc_config_t* lc_camera_generate_configuration(lc_camera_t* c)
    {
        if (!c) return nullptr;

        std::vector<StreamRole> roles = { StreamRole::Viewfinder };
        auto cfg = c->camera->generateConfiguration(roles);
        if (!cfg || cfg->empty()) return nullptr;

        auto* w = new lc_config_t();
        w->stream = cfg->at(0).stream();
        w->config = std::move(cfg);
        return w;
    }

    void lc_config_destroy(lc_config_t* cfg)
    {
        delete cfg;
    }

    int lc_config_set_size(lc_config_t* cfg, uint32_t width, uint32_t height)
    {
        if (!cfg || !cfg->config || cfg->config->empty()) return -1;
        cfg->config->at(0).size = { width, height };
        return 0;
    }

    int lc_config_set_pixel_format(lc_config_t* cfg, uint32_t fourcc)
    {
        if (!cfg || !cfg->config || cfg->config->empty()) return -1;
        cfg->config->at(0).pixelFormat = PixelFormat(fourcc);
        return 0;
    }

    int lc_config_set_sensor_mode(lc_config_t* cfg, uint32_t width, uint32_t height, uint32_t bit_depth)
    {
        if (!cfg || !cfg->config || width == 0 || height == 0 || bit_depth == 0) return -1;
        // The Raspberry Pi pipeline picks the sensor mode by these two; the rest is left as it would choose it.
        SensorConfiguration sensor;
        sensor.outputSize = { width, height };
        sensor.bitDepth = bit_depth;
        cfg->config->sensorConfig = sensor;
        return 0;
    }

    int lc_config_validate(lc_config_t* cfg)
    {
        if (!cfg || !cfg->config) return -1;
        return static_cast<int>(cfg->config->validate());
    }

    int lc_camera_configure(lc_camera_t* c, lc_config_t* cfg)
    {
        if (!c || !cfg || !cfg->config) return -1;

        int ret = c->camera->configure(cfg->config.get());
        if (ret < 0) return ret;

        cfg->stream = cfg->config->at(0).stream();
        c->stream = cfg->stream;
        return 0;
    }

    int lc_config_get_stride(lc_config_t* cfg, uint32_t* stride)
    {
        if (!cfg || !cfg->config || cfg->config->empty() || !stride) return -1;
        *stride = cfg->config->at(0).stride;
        return 0;
    }

    int lc_config_get_frame_size(lc_config_t* cfg, uint32_t* frame_size)
    {
        if (!cfg || !cfg->config || cfg->config->empty() || !frame_size) return -1;
        *frame_size = cfg->config->at(0).frameSize;
        return 0;
    }

    // --- Allocator ---

    lc_allocator_t* lc_allocator_create(lc_camera_t* c)
    {
        if (!c || !c->stream) return nullptr;

        auto* a = new lc_allocator_t();
        a->camera = c;
        a->stream = c->stream;
        a->allocator = std::make_unique<FrameBufferAllocator>(c->camera);
        return a;
    }

    void lc_allocator_destroy(lc_allocator_t* a)
    {
        if (!a) return;
        unmap_buffers(a);
        delete a;
    }

    int lc_allocator_allocate(lc_allocator_t* a)
    {
        if (!a || !a->stream) return -1;

        int ret = a->allocator->allocate(a->stream);
        if (ret < 0) return ret;

        a->buffers.clear();
        for (const auto& b : a->allocator->buffers(a->stream))
            a->buffers.push_back(b.get());

        return map_buffers(a);
    }

    int lc_allocator_buffer_count(lc_allocator_t* a)
    {
        if (!a) return -1;
        return static_cast<int>(a->buffers.size());
    }

    int lc_allocator_get_buffer(lc_allocator_t* a, int index, void** handle)
    {
        if (!a || !handle) return -1;
        if (index < 0 || static_cast<size_t>(index) >= a->buffers.size()) return -2;

        *handle = a->buffers[index];
        return 0;
    }

    int lc_allocator_get_buffer_plane_info(
        lc_allocator_t* a,
        void* handle,
        int plane_index,
        void** data,
        size_t* length)
    {
        if (!a || !handle || !data || !length) return -1;

        auto* buf = reinterpret_cast<FrameBuffer*>(handle);
        auto it = a->mapped.find(buf);
        if (it == a->mapped.end()) return -2;
        if (plane_index < 0 || static_cast<size_t>(plane_index) >= it->second.size()) return -3;

        const MappedPlane& mp = it->second[plane_index];
        *data = static_cast<uint8_t*>(mp.addr) + mp.offset;
        *length = buf->planes()[plane_index].length;
        return 0;
    }

    // --- Request ---

    lc_request_t* lc_request_create(lc_camera_t* c)
    {
        if (!c) return nullptr;

        auto req = c->camera->createRequest();
        if (!req) return nullptr;

        auto* r = new lc_request_t();
        r->camera = c;
        r->request = std::move(req);
        return r;
    }

    void lc_request_destroy(lc_request_t* r)
    {
        delete r;
    }

    int lc_request_attach_buffer(lc_request_t* r, void* handle)
    {
        if (!r || !r->camera || !r->camera->stream || !handle) return -1;

        auto* buf = reinterpret_cast<FrameBuffer*>(handle);

        // Store wrapper pointer in FrameBuffer cookie for callback recovery
        buf->setCookie(reinterpret_cast<uint64_t>(r));

        int ret = r->request->addBuffer(r->camera->stream, buf);
        if (ret < 0) return ret;

        r->buffer = buf;
        return 0;
    }

    int lc_request_reuse(lc_request_t* r)
    {
        if (!r || !r->request) return -1;
        r->request->reuse(Request::ReuseBuffers);
        return 0;
    }

    int lc_request_get_frame_info(lc_request_t* r, lc_frame_info_t* info)
    {
        if (!r || !r->request || !info) return -1;

        std::memset(info, 0, sizeof(*info));

        const ControlList& meta = r->request->metadata();
        if (meta.contains(controls::SensorTimestamp.id())) {
            auto tsOpt = meta.get(controls::SensorTimestamp);
            int64_t ts = tsOpt ? static_cast<int64_t>(*tsOpt) : 0LL;
            info->timestamp_us = static_cast<uint64_t>(ts / 1000LL);
        }

        if (r->buffer)
            info->sequence = r->buffer->metadata().sequence;

        if (r->camera && r->camera->stream) {
            const StreamConfiguration& sc = r->camera->stream->configuration();
            info->width = sc.size.width;
            info->height = sc.size.height;
            info->stride = sc.stride;
            info->pixel_format = sc.pixelFormat.fourcc();
        }

        return 0;
    }

    int lc_request_get_plane_data(
        lc_request_t* r,
        lc_allocator_t* a,
        int plane_index,
        void** data,
        size_t* length)
    {
        if (!r || !r->buffer || !a || !data || !length) return -1;
        return lc_allocator_get_buffer_plane_info(a, r->buffer, plane_index, data, length);
    }

} // extern "C"