
using System.Diagnostics;
using System.Text;

using WebGpuSharp;
using ImGuiNET;
using SDL2;

namespace Demo;

public class Application
{
    const string ApplicationName = "Wegpu IMGUI SDL2";
    int initialWidth = 1280, initialHeight = 720;
    TextureFormat applicationpreferredFormat = TextureFormat.BGRA8Unorm;

    nint sdlwindow;
    Surface surface;
    Instance instance;
    Adapter adapter;
    Device device;
    WebGpuSharp.Queue queue;

    bool shouldTermiate = false;

    public void Run()
    {
        // Setup Window
        {
            // Create WebGPU instance 
            instance = WebGPU.CreateInstance() ?? throw new Exception("Failed to create webgpu instance");

            // Create Adapter
            adapter = instance.RequestAdapterAsync(new()
            {
                PowerPreference = PowerPreference.HighPerformance,
                BackendType = BackendType.D3D12,
                CompatibleSurface = null,
            }).GetAwaiter().GetResult() ?? throw new Exception("Failed to acquire Adapter");
            SupportedLimits supportedLimits = adapter.GetLimits()!.Value;

            // Create Device
            DeviceDescriptor deviceDescriptor = new DeviceDescriptor()
            {
                RequiredLimits = new WGPUNullableRef<RequiredLimits>(
                    new RequiredLimits() { Limits = supportedLimits.Limits }
                    ),
                DefaultQueue = new QueueDescriptor(),
                UncapturedErrorCallback = (ErrorType type, ReadOnlySpan<byte> message) =>
                {
                    string str = $"{Enum.GetName(type)} : {Encoding.UTF8.GetString(message)}";

                    Console.Error.WriteLine(str);
                    Debug.WriteLine(str);
                },
                DeviceLostCallback = (DeviceLostReason lostReason, ReadOnlySpan<byte> message) =>
                {
                    string str = $"Device lost! Reason: {Enum.GetName(lostReason)} : {Encoding.UTF8.GetString(message)}";
                    Console.Error.WriteLine(str);
                    Debug.WriteLine(str);
                    Debugger.Break();
                }

            };
            device = adapter.RequestDeviceAsync(deviceDescriptor).GetAwaiter().GetResult() ?? throw new Exception("Failed to acquire Device");
                
            // Setup windowing with SDL. 
            _ = SDL.SDL_Init(SDL.SDL_INIT_EVERYTHING);
            // Create SDL window
            sdlwindow = SDL.SDL_CreateWindow(ApplicationName, 30, 30, (int)initialWidth, (int)initialHeight, SDL.SDL_WindowFlags.SDL_WINDOW_RESIZABLE | SDL.SDL_WindowFlags.SDL_WINDOW_SHOWN | SDL.SDL_WindowFlags.SDL_WINDOW_MAXIMIZED);
            // Create surface
            surface = CreateWebGPUSurfaceFromSDLWindow(instance, sdlwindow)!;
                
            // Call on surface resized with the initial window size
            SDL.SDL_GetWindowSize(sdlwindow, out initialWidth, out initialHeight);
            OnSurfaceResized((uint)initialWidth, (uint)initialHeight);
            
        }

        // Setup IMGUI
        {
            IntPtr context = ImGui.CreateContext();
            ImGui.SetCurrentContext(context);

            var io = ImGui.GetIO();
            io.ConfigFlags |= ImGuiConfigFlags.DockingEnable;

            var initInfo = new ImGui_Impl_WebGPUSharp.ImGui_ImplWGPU_InitInfo()
            {
                device = device,
                num_frames_in_flight = 3,
                rt_format = applicationpreferredFormat,
                depth_format = TextureFormat.Undefined,
            };

            ImGui_Impl_WebGPUSharp.Init(initInfo);
            ImGui_Impl_SDL2.Init(sdlwindow);

            io.Fonts.AddFontDefault();
            io.Fonts.Build();
        }

        while (!shouldTermiate)
        {
            // Perform Update
            {
                instance.ProcessEvents();

                // Inject input from sdl to imgui
                while (SDL.SDL_PollEvent(out var e) != 0)
                {
                    ImGui_Impl_SDL2.ProcessEvent(e);
                    // Close window
                    if (e.type == SDL.SDL_EventType.SDL_QUIT)
                    {
                        shouldTermiate = true;
                    }
                }

                ImGui_Impl_SDL2.NewFrame();
                ImGui_Impl_WebGPUSharp.NewFrame();
                ImGui.NewFrame();

                UI();

                ImGui.EndFrame();
            }

            // Perform rendering
            {
                SurfaceTexture surfaceTexture = surface.GetCurrentTexture();
                // Failed to get the surface texture. TODO handle
                if (surfaceTexture.Status != SurfaceGetCurrentTextureStatus.Success)
                    return;

                TextureViewDescriptor viewdescriptor = new()
                {
                    Format = surfaceTexture.Texture.GetFormat(),
                    Dimension = TextureViewDimension.D2,
                    MipLevelCount = 1,
                    BaseMipLevel = 0,
                    BaseArrayLayer = 0,
                    ArrayLayerCount = 1,
                    Aspect = TextureAspect.All,
                };
                TextureView textureView = surfaceTexture.Texture.CreateView(viewdescriptor) ?? throw new Exception("Failed to create texture view");
                
                // Command Encoder
                var commandEncoder = device.CreateCommandEncoder(new() { Label = "Main Command Encoder" });

                Span<RenderPassColorAttachment> colorAttachments = [
                    new(){
                        View = textureView,
                        ResolveTarget = default,
                        LoadOp = LoadOp.Clear,
                        StoreOp = StoreOp.Store,
                        ClearValue = new Color(0,0,0,1)
                    }
                ];

                // Render Imgui
                {
                    RenderPassDescriptor renderPassDesc = new()
                    {
                        label = "Pass IMGUI",
                        ColorAttachments = colorAttachments,
                        DepthStencilAttachment = null
                    };
                    var RenderPassEncoder = commandEncoder.BeginRenderPass(renderPassDesc);

                    ImGui.Render();
                    ImGui_Impl_WebGPUSharp.RenderDrawData(ImGui.GetDrawData(), RenderPassEncoder);

                    RenderPassEncoder.End();
                }

                // Finish Rendering
                var commandBuffer = commandEncoder.Finish(new() { });
                queue.Submit(commandBuffer);
                surface.Present();
            }
        }
    }


    void UI()
    {
        // Add UI here
        ImGui.ShowDemoWindow();
    }

    void OnSurfaceResized(uint width, uint height)
    {
        SurfaceConfiguration config = new()
        {
            Device = device,
            Format = applicationpreferredFormat,
            Width = width,
            Height = height,
            Usage = TextureUsage.RenderAttachment,
            PresentMode = PresentMode.Fifo,
            AlphaMode = CompositeAlphaMode.Auto
        };
        surface.Configure(config);
    }

    // only works with windows
    public static Surface? CreateWebGPUSurfaceFromSDLWindow(Instance instance, nint windowHandle)
    {
        unsafe
        {
            SDL.SDL_SysWMinfo info = new();
            SDL.SDL_GetVersion(out info.version);
            SDL.SDL_GetWindowWMInfo(windowHandle, ref info);

            if (info.subsystem == SDL.SDL_SYSWM_TYPE.SDL_SYSWM_WINDOWS)
            {
                var wsDescriptor = new WebGpuSharp.FFI.SurfaceDescriptorFromWindowsHWNDFFI()
                {
                    Value = new WebGpuSharp.FFI.SurfaceSourceWindowsHWNDFFI()
                    {
                        Hinstance = (void*)info.info.win.hinstance,
                        Hwnd = (void*)info.info.win.window,
                        Chain = new ChainedStruct()
                        {
                            SType = SType.SurfaceSourceWindowsHWND
                        }
                    }

                };
                SurfaceDescriptor descriptor_surface = new(ref wsDescriptor);
                return instance.CreateSurface(descriptor_surface);
            }

            throw new Exception("Platform not supported");
        }
    }
}