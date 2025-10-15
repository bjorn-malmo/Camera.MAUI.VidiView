using Android.Content;
using Android.Content.Res;
using Android.Graphics;
using Android.Hardware.Camera2;
using Android.Hardware.Camera2.Params;
using Android.Media;
using Android.OS;
using Android.Renderscripts;
using Android.Runtime;
using Android.Util;
using Android.Views;
using Android.Widget;
using Java.Util.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Diagnostics;
using System.Linq;
using Camera2 = Android.Hardware.Camera2;
using CameraCharacteristics = Android.Hardware.Camera2.CameraCharacteristics;
using Class = Java.Lang.Class;
using Rect = Android.Graphics.Rect;
using RectF = Android.Graphics.RectF;
using Size = Android.Util.Size;
using SizeF = Android.Util.SizeF;

namespace Camera.MAUI.Platforms.Android;

internal class MauiCameraView: GridLayout
{
    private readonly CameraView cameraView;
    private IExecutorService executorService;
    private bool started = false;
    private int frames = 0;
    private bool initiated = false;
    private bool snapping = false;
    private bool recording = false;
    private readonly Context context;

    private readonly TextureView textureView;
    public CameraCaptureSession previewSession;
    public MediaRecorder mediaRecorder;
    private CaptureRequest.Builder previewBuilder;
    private CameraDevice cameraDevice;
    private readonly MyCameraStateCallback stateListener;
    private Size videoSize;
    private CameraManager cameraManager;
    private AudioManager audioManager;
    private readonly System.Timers.Timer timer;
    private readonly SparseIntArray ORIENTATIONS = new();
    private readonly SparseIntArray ORIENTATIONSFRONT = new();
    private CameraCharacteristics camChars;
    private PreviewCaptureStateCallback sessionCallback;
    private byte[] capturePhoto = null;
    private bool captureDone = false;
    private readonly ImageAvailableListener photoListener;
    private HandlerThread backgroundThread;
    private Handler backgroundHandler;
    private ImageReader imgReader;

    private ILogger _logger = NullLogger.Instance;
    private Microsoft.Maui.Graphics.Rect _focusRect;

    public MauiCameraView(Context context, CameraView cameraView) : base(context)
    {
        this.context = context;
        this.cameraView = cameraView;

        textureView = new(context);
        timer = new(33.3);
        timer.Elapsed += Timer_Elapsed;
        stateListener = new MyCameraStateCallback(this);
        photoListener = new ImageAvailableListener(this);
        AddView(textureView);
        ORIENTATIONS.Append((int)SurfaceOrientation.Rotation0, 90);
        ORIENTATIONS.Append((int)SurfaceOrientation.Rotation90, 0);
        ORIENTATIONS.Append((int)SurfaceOrientation.Rotation180, 270);
        ORIENTATIONS.Append((int)SurfaceOrientation.Rotation270, 180);
        ORIENTATIONSFRONT.Append((int)SurfaceOrientation.Rotation0, 270);
        ORIENTATIONSFRONT.Append((int)SurfaceOrientation.Rotation90, 0);
        ORIENTATIONSFRONT.Append((int)SurfaceOrientation.Rotation180, 90);
        ORIENTATIONSFRONT.Append((int)SurfaceOrientation.Rotation270, 180);
        InitDevices();
    }

    private void InitDevices()
    {
        if (!initiated && cameraView != null)
        {
            cameraManager = (CameraManager)context.GetSystemService(Context.CameraService);
            audioManager = (AudioManager)context.GetSystemService(Context.AudioService);
            cameraView.Cameras.Clear();
            foreach (var id in cameraManager.GetCameraIdList())
            {
                var cameraInfo = new CameraInfo { DeviceId = id };
                var chars = cameraManager.GetCameraCharacteristics(id);
                if ((int)(chars.Get(CameraCharacteristics.LensFacing) as Java.Lang.Number) == (int)LensFacing.Back)
                {
                    cameraInfo.Name = "Back Camera";
                    cameraInfo.Position = CameraPosition.Back;
                }
                else if ((int)(chars.Get(CameraCharacteristics.LensFacing) as Java.Lang.Number) == (int)LensFacing.Front)
                {
                    cameraInfo.Name = "Front Camera";
                    cameraInfo.Position = CameraPosition.Front;
                }
                else
                {
                    cameraInfo.Name = "Camera " + id;
                    cameraInfo.Position = CameraPosition.Unknow;
                }
                cameraInfo.MinZoomFactor = Math.Max(CameraView.RestrictMinimumZoomFactor, 1f);
                cameraInfo.MaxZoomFactor = Math.Min(CameraView.RestrictMaximumZoomFactor, (float)(chars.Get(CameraCharacteristics.ScalerAvailableMaxDigitalZoom) as Java.Lang.Number));
                cameraInfo.HasFlashUnit = (bool)(chars.Get(CameraCharacteristics.FlashInfoAvailable) as Java.Lang.Boolean);
                cameraInfo.AvailableResolutions = new();
                try
                {
                    float[] maxFocus = (float[])chars.Get(CameraCharacteristics.LensInfoAvailableFocalLengths);
                    SizeF size = (SizeF)chars.Get(CameraCharacteristics.SensorInfoPhysicalSize);
                    cameraInfo.HorizontalViewAngle = (float)(2 * Math.Atan(size.Width / (maxFocus[0] * 2)));
                    cameraInfo.VerticalViewAngle = (float)(2 * Math.Atan(size.Height / (maxFocus[0] * 2)));
                }
                catch { }
                try
                {
                    StreamConfigurationMap map = (StreamConfigurationMap)chars.Get(CameraCharacteristics.ScalerStreamConfigurationMap);
                    foreach (var s in map.GetOutputSizes(Class.FromType(typeof(ImageReader))))
                        cameraInfo.AvailableResolutions.Add(new(s.Width, s.Height));
                }
                catch
                {
                    if (cameraInfo.Position == CameraPosition.Back)
                        cameraInfo.AvailableResolutions.Add(new(1920, 1080));
                    cameraInfo.AvailableResolutions.Add(new(1280, 720));
                    cameraInfo.AvailableResolutions.Add(new(640, 480));
                    cameraInfo.AvailableResolutions.Add(new(352, 288));
                }
                cameraView.Cameras.Add(cameraInfo);
                }
            if (OperatingSystem.IsAndroidVersionAtLeast(30))
            {
                cameraView.Microphones.Clear();
                foreach (var device in audioManager.Microphones)
                {
                    cameraView.Microphones.Add(new MicrophoneInfo { Name = "Microphone " + device.Type.ToString() + " " + device.Address, DeviceId = device.Id.ToString() });
                }
            }
            //Microphone = Micros.FirstOrDefault();
            executorService = Executors.NewSingleThreadExecutor();

            initiated = true;
            cameraView.RefreshDevices();
        }
    }

    internal void SetLogger(ILoggerFactory loggerFactory)
    {
        if (loggerFactory != null)
        {
            _logger = loggerFactory.CreateLogger<MauiCameraView>();
        }
    }

    internal async Task<CameraResult> StartRecordingAsync(string file, Microsoft.Maui.Graphics.Size Resolution, RecordingParameters options = null)
    {
        _logger.LogInformation("Start recording");

        var result = CameraResult.Success;
        if (initiated && !recording)
        {
            var recordAudio = options?.RecordAudio ?? false;
            if (await CameraView.RequestPermissions(recordAudio, true))
            {
                if (started) StopCamera();
                if (cameraView.Camera != null)
                {
                    try
                    {
                        camChars = cameraManager.GetCameraCharacteristics(cameraView.Camera.DeviceId);

                        StreamConfigurationMap map = (StreamConfigurationMap)camChars.Get(CameraCharacteristics.ScalerStreamConfigurationMap);
                        //videoSize = ChooseVideoSize(map.GetOutputSizes(Class.FromType(typeof(ImageReader))));
                        recording = true;

                        if (File.Exists(file)) File.Delete(file);

                        if (OperatingSystem.IsAndroidVersionAtLeast(31))
                            mediaRecorder = new MediaRecorder(context);
                        else
                            mediaRecorder = new MediaRecorder();

                        if (recordAudio)
                        {
                            audioManager.Mode = Mode.Normal;
                            mediaRecorder.SetAudioSource(AudioSource.Mic);
                        }
                        mediaRecorder.SetVideoSource(VideoSource.Surface);
                        mediaRecorder.SetOutputFormat(OutputFormat.Mpeg4);
                        mediaRecorder.SetOutputFile(file);
                        //mediaRecorder.SetVideoEncodingBitRate(10_000_000);
                        mediaRecorder.SetVideoFrameRate(options?.MaxFrameRate ?? 30);

                        Size desiredSize;
                        if (Resolution.Width > 0 && Resolution.Height > 0)
                            desiredSize = ChooseClosestMatch(map.GetOutputSizes(Class.FromType(typeof(ImageReader))), new((int)Resolution.Width, (int)Resolution.Height));
                        else
                            desiredSize = ChooseMaximum(map.GetOutputSizes(Class.FromType(typeof(ImageReader))));
                        mediaRecorder.SetVideoSize(desiredSize.Width, desiredSize.Height);

                        VideoEncoder videoEncoder;
                        AudioEncoder audioEncoder;

                        //mediaRecorder.SetVideoEncoder(VideoEncoder.H264);
                        //mediaRecorder.SetAudioEncoder(AudioEncoder.Aac);
                        if (false 
                            && options?.SupportedVideoCodecs?.Contains("AC01", StringComparer.OrdinalIgnoreCase) == true
                            && OperatingSystem.IsAndroidVersionAtLeast(33))
                        {
                            videoEncoder = VideoEncoder.Av1;
                            audioEncoder = AudioEncoder.Aac;
                        }
                        else if (options?.SupportedVideoCodecs?.Contains("HEVC", StringComparer.OrdinalIgnoreCase) == true)
                        {
                            videoEncoder = VideoEncoder.Hevc;
                            audioEncoder = AudioEncoder.Aac;
                        }
                        else
                        {
                            videoEncoder = VideoEncoder.H264;
                            audioEncoder = AudioEncoder.Aac;
                        }

                        var bitRate = GetBitrateFromSize(videoEncoder, desiredSize);
                        mediaRecorder.SetVideoEncodingBitRate(bitRate);
                        mediaRecorder.SetVideoEncoder(videoEncoder);
                        if (recordAudio)
                        {
                            mediaRecorder.SetAudioEncoder(audioEncoder);
                        }

                        _logger.LogDebug("Recording options: {videoEncoder} ({w} x {h} @{fps} Hz (max), {mbit} Mbit/s)", videoEncoder, desiredSize.Width, desiredSize.Height, options?.MaxFrameRate ?? 30, bitRate / 131072);

                        //IWindowManager windowManager = context.GetSystemService(Context.WindowService).JavaCast<IWindowManager>();


                        //HO changed
                        //int rotation = (int)windowManager.DefaultDisplay.Rotation;
                        //int rotation = options?.RotationRelativeToPortrait ?? (int)windowManager.DefaultDisplay.Rotation;

                        //       int orientation = cameraView.Camera.Position == CameraPosition.Back ? orientation = ORIENTATIONS.Get(rotation) : orientation = ORIENTATIONSFRONT.Get(rotation);
                        mediaRecorder.SetOrientationHint(0);
                        mediaRecorder.Prepare();

                        if (OperatingSystem.IsAndroidVersionAtLeast(28))
                            cameraManager.OpenCamera(cameraView.Camera.DeviceId, executorService, stateListener);
                        else
                            cameraManager.OpenCamera(cameraView.Camera.DeviceId, stateListener, null);
                        started = true;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to start recording");
                        result = CameraResult.AccessError;
                    }
                }
                else
                    result = CameraResult.NoCameraSelected;
            }
            else
            {
                result = CameraResult.AccessDenied;
            }
        }
        else
            result = CameraResult.NotInitiated;

        if (result == CameraResult.Success)
        {
            _logger.LogDebug("Recording started");
        }
        else
        {
            _logger.LogWarning("Failed to start recording: {Result}", result);
        }

        return result;
    }

    private int GetBitrateFromSize(VideoEncoder encoder, Size desiredSize)
    {
        if (desiredSize.Height <= 720)
            return 7;
        else if (desiredSize.Height <= 1080)
            return 14;
        else 
            return 20;
    }

    private void StartPreview()
    {
        while (textureView.SurfaceTexture == null || !textureView.IsAvailable) Thread.Sleep(100);
        SurfaceTexture texture = textureView.SurfaceTexture;
        texture.SetDefaultBufferSize(videoSize.Width, videoSize.Height);

        previewBuilder = cameraDevice.CreateCaptureRequest(recording ? CameraTemplate.Record : CameraTemplate.Preview);
        var surfaces = new List<OutputConfiguration>();
        var surfaces26 = new List<Surface>();
        var previewSurface = new Surface(texture);
        surfaces.Add(new OutputConfiguration(previewSurface));
        surfaces26.Add(previewSurface);
        previewBuilder.AddTarget(previewSurface);
        if (imgReader != null)
        {
            surfaces.Add(new OutputConfiguration(imgReader.Surface));
            surfaces26.Add(imgReader.Surface);
        }
        if (mediaRecorder != null)
        {
            surfaces.Add(new OutputConfiguration(mediaRecorder.Surface));
            surfaces26.Add(mediaRecorder.Surface);
            previewBuilder.AddTarget(mediaRecorder.Surface);
        }

        sessionCallback = new PreviewCaptureStateCallback(this);
        if (OperatingSystem.IsAndroidVersionAtLeast(28))
        {
            SessionConfiguration config = new((int)SessionType.Regular, surfaces, executorService, sessionCallback);
            cameraDevice.CreateCaptureSession(config);
        }
        else
        {
#pragma warning disable CS0618 // El tipo o el miembro están obsoletos
            cameraDevice.CreateCaptureSession(surfaces26, sessionCallback, null);
#pragma warning restore CS0618 // El tipo o el miembro están obsoletos
        }
    }
    private void UpdatePreview()
    {
        if (null == cameraDevice)
            return;

        try
        {
            previewBuilder.Set(CaptureRequest.ControlMode, Java.Lang.Integer.ValueOf((int)ControlMode.Auto));
            //Rect m = (Rect)camChars.Get(CameraCharacteristics.SensorInfoActiveArraySize);
            //videoSize = new Size(m.Width(), m.Height());
            //AdjustAspectRatio(videoSize.Width, videoSize.Height);
            AdjustAspectRatio(videoSize.Width, videoSize.Height);
            SetZoomFactor(cameraView.ZoomFactor);
            //previewSession.SetRepeatingRequest(previewBuilder.Build(), null, null);
            if (recording) 
                mediaRecorder?.Start();
        }
        catch (CameraAccessException e)
        {
            e.PrintStackTrace();
        }
    }
    internal async Task<CameraResult> StartCameraAsync(Microsoft.Maui.Graphics.Size PhotosResolution)
    {
        _logger.LogInformation("Start camera");

        var result = CameraResult.Success;
        if (initiated)
        {
            if (await CameraView.RequestPermissions())
            {
                if (started) StopCamera();
                if (cameraView.Camera != null)
                {
                    try
                    {
                        camChars = cameraManager.GetCameraCharacteristics(cameraView.Camera.DeviceId);

                        StreamConfigurationMap map = (StreamConfigurationMap)camChars.Get(CameraCharacteristics.ScalerStreamConfigurationMap);

                        //videoSize = ChooseVideoSize(map.GetOutputSizes(Class.FromType(typeof(ImageReader))));
                        videoSize = ChoosePreviewSize(map.GetOutputSizes(Class.FromType(typeof(ImageReader))));

                        //var maxVideoSize = ChooseMaxVideoSize(map.GetOutputSizes(Class.FromType(typeof(ImageReader))));
                        var photoSize = ChoosePhotoResolution(map);
                        if (PhotosResolution.Width != 0 && PhotosResolution.Height != 0)
                            photoSize = new((int)PhotosResolution.Width, (int)PhotosResolution.Height);

                        imgReader = ImageReader.NewInstance(photoSize.Width, photoSize.Height, ImageFormatType.Jpeg, 1);
                        backgroundThread = new HandlerThread("CameraBackground");
                        backgroundThread.Start();
                        backgroundHandler = new Handler(backgroundThread.Looper);
                        imgReader.SetOnImageAvailableListener(photoListener, backgroundHandler);

                        if (OperatingSystem.IsAndroidVersionAtLeast(28))
                            cameraManager.OpenCamera(cameraView.Camera.DeviceId, executorService, stateListener);
                        else
                            cameraManager.OpenCamera(cameraView.Camera.DeviceId, stateListener, null);
                        timer.Start();

                        started = true;
                    }
                    catch
                    {
                        result = CameraResult.AccessError;
                    }
                }
                else
                    result = CameraResult.NoCameraSelected;
            }
            else
                result = CameraResult.AccessDenied;
        }
        else
            result = CameraResult.NotInitiated;

        if (result == CameraResult.Success)
        {
            _logger.LogDebug("Camera started");
        }
        else
        {
            _logger.LogWarning("Failed to start camera: {Result}", result);
            System.Diagnostics.Debug.Assert(false, $"Failed to start camera: {result}");
        }

        return result;
    }
    internal Task<CameraResult> StopRecordingAsync()
    {
        // We never restart the camera here.
        if (recording)
        {
            recording = false;
            StopCamera();
        }

        return Task.FromResult(CameraResult.Success);
    }

    internal CameraResult StopCamera()
    {
        _logger.LogInformation("Stop camera");

        CameraResult result = CameraResult.Success;
        if (initiated)
        {
            timer.Stop();
            try
            {
                mediaRecorder?.Stop();
                mediaRecorder?.Dispose();
            } catch { }
            try
            {
                backgroundThread?.QuitSafely();
                backgroundThread?.Join();
                backgroundThread = null;
                backgroundHandler = null;
                imgReader?.Dispose();
                imgReader = null;
            }
            catch { }
            try
            {
                previewSession?.StopRepeating();
                previewSession?.AbortCaptures();
                previewSession?.Dispose();
            } catch { }
            try
            {
                cameraDevice?.Close();
                cameraDevice?.Dispose();
            } catch { }
            previewSession = null;
            cameraDevice = null;
            previewBuilder = null;
            mediaRecorder = null;
            started = false;
            recording = false;
        }
        else
            result = CameraResult.NotInitiated;
        return result;
    }
    internal void DisposeControl()
    {
        try
        {
            if (started) StopCamera();
            executorService?.Shutdown();
            executorService?.Dispose();
            RemoveAllViews();
            textureView?.Dispose();
            timer?.Dispose();
            Dispose();
        }
        catch { }
    }
    private void ProccessQR()
    {
        Task.Run(() =>
        {
            Bitmap bitmap = TakeSnap();
            if (bitmap != null)
            {
                System.Diagnostics.Debug.WriteLine($"Processing QR ({bitmap.Width}x{bitmap.Height}) " + DateTime.Now.ToString("mm:ss:fff"));
                cameraView.DecodeBarcode(bitmap);
                bitmap.Dispose();
                System.Diagnostics.Debug.WriteLine("QR Processed " + DateTime.Now.ToString("mm:ss:fff"));
            }
            lock (cameraView.currentThreadsLocker) cameraView.currentThreads--;
        });
    }
    private void RefreshSnapShot()
    {
        cameraView.RefreshSnapshot(GetSnapShot(cameraView.AutoSnapShotFormat, true));
    }

    private void Timer_Elapsed(object sender, System.Timers.ElapsedEventArgs e)
    {
        if (!snapping && cameraView != null && cameraView.AutoSnapShotSeconds > 0 && (DateTime.Now - cameraView.lastSnapshot).TotalSeconds >= cameraView.AutoSnapShotSeconds)
        {
            Task.Run(() => RefreshSnapShot());
        }
        else if (cameraView.BarCodeDetectionEnabled)
        {
            frames++;
            if (frames >= cameraView.BarCodeDetectionFrameRate)
            {
                bool processQR = false;
                lock (cameraView.currentThreadsLocker)
                {
                    if (cameraView.currentThreads < cameraView.BarCodeDetectionMaxThreads)
                    {
                        cameraView.currentThreads++;
                        processQR = true;
                    }
                }
                if (processQR)
                {
                    ProccessQR();
                    frames = 0;
                }
            }
        }

    }

    private Bitmap TakeSnap()
    {
        Bitmap bitmap = null;
        try
        {
            MainThread.InvokeOnMainThreadAsync(() => { bitmap = textureView.GetBitmap(null); bitmap = textureView.Bitmap; }).Wait();
            if (bitmap != null)
            {
                int oriWidth = bitmap.Width;
                int oriHeight = bitmap.Height;

                bitmap = Bitmap.CreateBitmap(bitmap, 0, 0, bitmap.Width, bitmap.Height, textureView.GetTransform(null), false);
                float xscale = (float)oriWidth / bitmap.Width;
                float yscale = (float)oriHeight / bitmap.Height;
                bitmap = Bitmap.CreateBitmap(bitmap, (bitmap.Width - Width) / 2, (bitmap.Height - Height) / 2, Width, Height);
                if (textureView.ScaleX == -1)
                {
                    Matrix matrix = new();
                    matrix.PreScale(-1, 1);
                    bitmap = Bitmap.CreateBitmap(bitmap, 0, 0, bitmap.Width, bitmap.Height, matrix, false);
                }
            }
        }
        catch { }
        return bitmap;
    }
    internal async Task<System.IO.Stream> TakePhotoAsync(ImageFormat imageFormat, int? deviceRotation)
    {
        MemoryStream stream = null;
        if (started && !recording)
        {
            CaptureRequest.Builder singleRequest = cameraDevice.CreateCaptureRequest(CameraTemplate.StillCapture);
            captureDone = false;
            capturePhoto = null;

            if (cameraView.Camera.HasFlashUnit && cameraView.TorchEnabled)
            {
                // Support taking photo with torch on
                singleRequest.Set(CaptureRequest.ControlAeMode, (int)ControlAEMode.On);
                singleRequest.Set(CaptureRequest.FlashMode, (int)Camera2.FlashMode.Torch);
            }
            else
            {
                if (cameraView.Camera.HasFlashUnit)
                {
                    switch (cameraView.FlashMode)
                    {
                        case FlashMode.Auto:
                            singleRequest.Set(CaptureRequest.FlashMode, (int)ControlAEMode.OnAutoFlash);
                            break;
                        case FlashMode.Enabled:
                            singleRequest.Set(CaptureRequest.FlashMode, (int)ControlAEMode.On);
                            break;
                        case FlashMode.Disabled:
                            singleRequest.Set(CaptureRequest.FlashMode, (int)ControlAEMode.Off);
                            break;
                    }
                }
            }

            int rotation = GetJpegOrientation(deviceRotation);
            singleRequest.Set(CaptureRequest.JpegOrientation, rotation);

            var destZoom = Math.Clamp(cameraView.ZoomFactor, 1, Math.Min(6, cameraView.Camera.MaxZoomFactor)) - 1;
            Rect m = (Rect)camChars.Get(CameraCharacteristics.SensorInfoActiveArraySize);
            int minW = (int)(m.Width() / (cameraView.Camera.MaxZoomFactor));
            int minH = (int)(m.Height() / (cameraView.Camera.MaxZoomFactor));
            int newWidth = (int)(m.Width() - (minW * destZoom));
            int newHeight = (int)(m.Height() - (minH * destZoom));
            Rect zoomArea = new((m.Width() - newWidth) / 2, (m.Height() - newHeight) / 2, newWidth, newHeight);
            singleRequest.Set(CaptureRequest.ScalerCropRegion, zoomArea);

            singleRequest.AddTarget(imgReader.Surface);
            try
            {
                previewSession.Capture(singleRequest.Build(), null, null);
                while (!captureDone) await Task.Delay(10);
                if (capturePhoto != null)
                {
                    if (textureView.ScaleX == -1 || imageFormat != ImageFormat.JPEG)
                    {
                        Bitmap bitmap = BitmapFactory.DecodeByteArray(capturePhoto, 0, capturePhoto.Length);
                        if (bitmap != null)
                        {
                            if (textureView.ScaleX == -1)
                            {
                                Matrix matrix = new();
                                matrix.PreRotate(rotation);
                                matrix.PostScale(-1, 1);
                                bitmap = Bitmap.CreateBitmap(bitmap, 0, 0, bitmap.Width, bitmap.Height, matrix, false);
                            }
                            var iformat = imageFormat switch
                            {
                                ImageFormat.JPEG => Bitmap.CompressFormat.Jpeg,
                                _ => Bitmap.CompressFormat.Png
                            };
                            stream = new();
                            bitmap.Compress(iformat, 100, stream);
                            stream.Position = 0;
                        }
                    }
                    else
                    {
                        stream = new();
                        stream.Write(capturePhoto);
                        stream.Position = 0;
                    }
                }
            }
            catch(Java.Lang.Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(ex.StackTrace);
            }
        }

        return stream;
    }
    internal ImageSource GetSnapShot(ImageFormat imageFormat, bool auto = false)
    {
        ImageSource result = null;

        if (started && !snapping)
        {
            snapping = true;
            Bitmap bitmap = TakeSnap();

            if (bitmap != null)
            {
                var iformat = imageFormat switch
                {
                    ImageFormat.JPEG => Bitmap.CompressFormat.Jpeg,
                    _ => Bitmap.CompressFormat.Png
                };
                MemoryStream stream = new();
                bitmap.Compress(iformat, 100, stream);
                stream.Position = 0;
                if (auto)
                {
                    if (cameraView.AutoSnapShotAsImageSource)
                        result = ImageSource.FromStream(() => stream);
                    cameraView.SnapShotStream?.Dispose();
                    cameraView.SnapShotStream = stream;
                }
                else
                    result = ImageSource.FromStream(() => stream);
                bitmap.Dispose();
            }
            snapping = false;
        }
        return result;
    }

    internal bool SaveSnapShot(ImageFormat imageFormat, string SnapFilePath, int? deviceRotation)
    {
        bool result = true;

        if (started && !snapping)
        {
            snapping = true;
            Bitmap bitmap = TakeSnap();
            if (bitmap != null)
            {
                bitmap = RotateSnapshot(bitmap, deviceRotation);

                if (File.Exists(SnapFilePath)) File.Delete(SnapFilePath);
                var iformat = imageFormat switch
                {
                    ImageFormat.JPEG => Bitmap.CompressFormat.Jpeg,
                    _ => Bitmap.CompressFormat.Png
                };
                using FileStream stream = new(SnapFilePath, FileMode.OpenOrCreate);
                bitmap.Compress(iformat, 80, stream);
                stream.Close();
                bitmap.Dispose();
            }
            snapping = false;
        }
        else
            result = false;

        return result;
    }

    private Bitmap RotateSnapshot(Bitmap bitmap, int? deviceRotation)
    {
        if (deviceRotation == null)
        {
            IWindowManager windowManager = context.GetSystemService(Context.WindowService).JavaCast<IWindowManager>();
            deviceRotation = (int)windowManager.DefaultDisplay.Rotation;
        }

        if (deviceRotation.Value != 0)
        {
            Matrix matrix = new();
            //dont now why but using 360 - gives the correct value together with the recording
            matrix.PostRotate(360 - deviceRotation.Value, bitmap.Width / 2, bitmap.Height / 2);

            //HO Recommended default is to set filter to 'true' as the cost of bilinear filtering is typically minimal and the improved image quality is significant.
            var prevBimap = bitmap;
            bitmap = Bitmap.CreateBitmap(bitmap, 0, 0, bitmap.Width, bitmap.Height, matrix, true);
            prevBimap.Dispose();
        }

        return bitmap;
    }

    public void UpdateMirroredImage()
    {
        if (cameraView != null && textureView != null)
        {
            if (cameraView.MirroredImage) 
                textureView.ScaleX = -1;
            else
                textureView.ScaleX = 1;
        }
    }
    internal void UpdateTorch()
    {
        if (cameraView.Camera != null && cameraView.Camera.HasFlashUnit)
        {
            if (started)
            {
                previewBuilder.Set(CaptureRequest.ControlAeMode, (int)ControlAEMode.On);

                //OnePlus, Samsung does not work with torch if OnAlwaysFlash is set
                //previewBuilder.Set(CaptureRequest.FlashMode, cameraView.TorchEnabled ? (int)ControlAEMode.OnAutoFlash : (int)ControlAEMode.Off);
                previewBuilder.Set(CaptureRequest.FlashMode, cameraView.TorchEnabled ? (int)Camera2.FlashMode.Torch : (int)Camera2.FlashMode.Off);

                previewSession.SetRepeatingRequest(previewBuilder.Build(), null, null);
            }
            else if (initiated)
                cameraManager.SetTorchMode(cameraView.Camera.DeviceId, cameraView.TorchEnabled);
        }
    }
    internal void UpdateFlashMode()
    {
        if (previewSession != null && previewBuilder != null && cameraView.Camera != null && cameraView != null)
        {
            try
            {
                if (cameraView.Camera.HasFlashUnit)
                {
                    switch (cameraView.FlashMode)
                    {
                        case FlashMode.Auto:
                            previewBuilder.Set(CaptureRequest.ControlAeMode, (int)ControlAEMode.OnAutoFlash);
                            previewSession.SetRepeatingRequest(previewBuilder.Build(), null, null);
                            break;
                        case FlashMode.Enabled:
                            previewBuilder.Set(CaptureRequest.ControlAeMode, (int)ControlAEMode.On);
                            previewSession.SetRepeatingRequest(previewBuilder.Build(), null, null);
                            break;
                        case FlashMode.Disabled:
                            previewBuilder.Set(CaptureRequest.ControlAeMode, (int)ControlAEMode.Off);
                            previewSession.SetRepeatingRequest(previewBuilder.Build(), null, null);
                            break;
                    }
                }
            }
            catch (System.Exception)
            {
            }
        }
    }
    internal void SetZoomFactor(float zoom)
    {
        if (previewSession != null && previewBuilder != null && cameraView.Camera != null)
        {
            //if (OperatingSystem.IsAndroidVersionAtLeast(30))
            //{
            //previewBuilder.Set(CaptureRequest.ControlZoomRatio, Math.Max(Camera.MinZoomFactor, Math.Min(zoom, Camera.MaxZoomFactor)));
            //}
            //var destZoom = Math.Clamp(zoom, 1, Math.Min(6, cameraView.Camera.MaxZoomFactor)) - 1;
            //Rect m = (Rect)camChars.Get(CameraCharacteristics.SensorInfoActiveArraySize);
            //int minW = (int)(m.Width() / (cameraView.Camera.MaxZoomFactor));
            //int minH = (int)(m.Height() / (cameraView.Camera.MaxZoomFactor));
            //int newWidth = (int)(m.Width() - (minW * destZoom));
            //int newHeight = (int)(m.Height() - (minH * destZoom));
            //Rect zoomArea = new((m.Width()-newWidth)/2, (m.Height()-newHeight)/2, newWidth, newHeight);
            //previewBuilder.Set(CaptureRequest.ScalerCropRegion, zoomArea);
            //previewSession.SetRepeatingRequest(previewBuilder.Build(), null, null);

            var destZoom = Math.Clamp(cameraView.ZoomFactor, 1, cameraView.Camera.MaxZoomFactor);
            if (OperatingSystem.IsAndroidVersionAtLeast(30))
            {
                previewBuilder.Set(CaptureRequest.ControlZoomRatio, destZoom);
            }
            else
            {
                Rect sensorRect = (Rect)camChars.Get(CameraCharacteristics.SensorInfoActiveArraySize);
                Rect zoomedSensorArea = CalculateScalerRect(sensorRect, destZoom);
                previewBuilder.Set(CaptureRequest.ScalerCropRegion, zoomedSensorArea);
            }

            previewSession.SetRepeatingRequest(previewBuilder.Build(), null, null);
            //HO focus must be updated as it is a point relative to the preview not the sensor
            SetFocus(_focusRect);


        }
    }
    internal void ForceAutoFocus()
    {
        if (previewSession != null && previewBuilder != null && cameraView.Camera != null)
        {
            previewBuilder.Set(CaptureRequest.ControlAfMode, Java.Lang.Integer.ValueOf((int)ControlAFMode.Off));
            previewBuilder.Set(CaptureRequest.ControlAfTrigger, Java.Lang.Integer.ValueOf((int)ControlAFTrigger.Cancel));
            previewSession.SetRepeatingRequest(previewBuilder.Build(), null, null);
            previewBuilder.Set(CaptureRequest.ControlAfMode, Java.Lang.Integer.ValueOf((int)ControlAFMode.Auto));
            previewBuilder.Set(CaptureRequest.ControlAfTrigger, Java.Lang.Integer.ValueOf((int)ControlAFTrigger.Start));
            previewSession.SetRepeatingRequest(previewBuilder.Build(), null, null);

        }
    }

    public class SetFocusContext
    {
        public CameraCharacteristics CameraCharacteristics { get; set; }
        public Rect ActiveArraySize { get; set; }
        public int ControlMaxRegionsAf { get; set; }
        public Java.Lang.Object OrgAfRegions { get; set; }
    }

    class ManualFocusEngaged_CaptureCallback : CameraCaptureSession.CaptureCallback
    {
        readonly MauiCameraView _mauiCameraView;
        readonly ILogger _logger;
        readonly SetFocusContext _setFocusContext;

        public ManualFocusEngaged_CaptureCallback(MauiCameraView mauiCameraView, ILogger logger, SetFocusContext setFocusContext)
        {
            _mauiCameraView = mauiCameraView;
            _logger = logger;
            _setFocusContext = setFocusContext;
        }

        public override void OnCaptureCompleted(CameraCaptureSession session, CaptureRequest request, TotalCaptureResult result)
        {
            try
            {
                base.OnCaptureCompleted(session, request, result);
                _mauiCameraView.ManualFocusEngaged = false;

                var requestTag = (string)request.Tag;
                if (requestTag == "FOCUS_TAG")
                {
                    //In some devices(Sony xperia G8142 etc..),CaptureRequest.CONTROL_AF_TRIGGER can not be set to null,or the camera throws an error. but how to solve this, I have no idea..
                    //I found that setting CONTROL_AF_TRIGGER to CONTROL_AF_TRIGGER_IDLE instead of null works on Xperia devices.
                    _mauiCameraView.previewBuilder.Set(CaptureRequest.ControlAfTrigger, (int)ControlAFTrigger.Idle);

                    // (int)ControlAFTrigger.Idle);// bControlAFMode.T CaptureRequest.CONTROL_AF_TRIGGER, null);
                    _mauiCameraView.previewSession.SetRepeatingRequest(_mauiCameraView.previewBuilder.Build(), null, null);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"{nameof(OnCaptureCompleted)}: failed: {ex.Message}");
            }

        }
        public override void OnCaptureFailed(CameraCaptureSession session, CaptureRequest request, CaptureFailure failure)
        {
            try
            {
                base.OnCaptureFailed(session, request, failure);
                var failureReason = (string)failure.Reason.ToString();
                _logger.LogWarning($"{nameof(OnCaptureFailed)}: {failureReason}");
                _mauiCameraView.ManualFocusEngaged = false;
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"{nameof(OnCaptureFailed)}: failed: {ex.Message}");
            }
        }
    }

    SetFocusContext _setFocusContext = new SetFocusContext();
    public bool ManualFocusEngaged { get; set; }
    private static Microsoft.Maui.Graphics.Rect CalculateScalerRect(Rect sensorRect, Single zoomFactor)
    {
        Rect m = sensorRect;

        var ratio = 1 / zoomFactor;
        var sensorRectWidth = sensorRect.Right - sensorRect.Left;
        var sensorRectHeight = sensorRect.Bottom - sensorRect.Top;

        var w = (int)(sensorRectWidth * ratio);
        var newLeft = (sensorRectWidth - w) / 2;
        var h = (int)(sensorRectHeight * ratio);
        var newTop = (sensorRectHeight - h) / 2;
        return new Microsoft.Maui.Graphics.Rect(newLeft, newTop, w + newLeft, h + newTop);
    }

    internal bool SetFocus(Microsoft.Maui.Graphics.Rect rect)// (Microsoft.Maui.Graphics.PointF pointRelativeToVisibleArea)
    {
        // Get characteristics
        if (_setFocusContext.CameraCharacteristics == null)
        {
            _setFocusContext.CameraCharacteristics = camChars;
            _setFocusContext.ActiveArraySize = (Rect)_setFocusContext.CameraCharacteristics.Get(CameraCharacteristics.SensorInfoActiveArraySize);
            _setFocusContext.ControlMaxRegionsAf = (int)_setFocusContext.CameraCharacteristics.Get(CameraCharacteristics.ControlMaxRegionsAf);
            if (_setFocusContext.ControlMaxRegionsAf > 0)
            {
                _setFocusContext.OrgAfRegions = previewBuilder.Get(CaptureRequest.ControlAfRegions);
            }
        }

        if (_setFocusContext.ControlMaxRegionsAf == 0)
        {
            //no af regions allowed return
            _focusRect = Microsoft.Maui.Graphics.Rect.Zero;
            return false;
        }

        _focusRect = rect;
        if (rect == Microsoft.Maui.Graphics.Rect.Zero)
        { //revert to autofocus
            //HO must set it to the original regions null does not work
            previewBuilder.Set(CaptureRequest.ControlAfRegions, _setFocusContext.OrgAfRegions);
            //previewBuilder.Set(CaptureRequest.ControlAfMode, (int)ControlAFMode.ContinuousPicture);// (int)ControlAFTrigger.Idle);// bControlAFMode.T CaptureRequest.CONTROL_AF_TRIGGER, null);
            previewSession.SetRepeatingRequest(previewBuilder.Build(), null, null);
            return true;
        }


        //HO in this code we assume:
        //   - cameraView.Height always matches sensor.Width
        //   - sensor is rotated 90 degrees relative to phone up
        //   - what we se is the middle of the sensor

        var activeArraySize = _setFocusContext.ActiveArraySize;

        var fakeCameraViewWidthInCameraViewUnits = (cameraView.Height / activeArraySize.Width()) * activeArraySize.Height();
        var fakeXOffsetInCameraViewUnits = (fakeCameraViewWidthInCameraViewUnits - cameraView.Width) / 2;

        var fakeCameraViewX = (rect.Center.X + fakeXOffsetInCameraViewUnits);
        var fakeCameraViewY = (rect.Center.Y);

        //calculate focusrect if somebody wants it


        var sensorTouchAreaExtent = (activeArraySize.Width() * (rect.Height / cameraView.Height));
        var usedSensorArray = new Rect(0, 0, activeArraySize.Width(), activeArraySize.Height());

        //handle zoom
        if (cameraView.ZoomFactor != 1f)
        {
            usedSensorArray = CalculateScalerRect(usedSensorArray, cameraView.ZoomFactor);
            sensorTouchAreaExtent = sensorTouchAreaExtent / cameraView.ZoomFactor;
        }
        //rotate
        var sensorX = (usedSensorArray.Left + (usedSensorArray.Width() * (fakeCameraViewY / cameraView.Height)));
        var sensorY = usedSensorArray.Top + (usedSensorArray.Height() * (1 - (fakeCameraViewX / fakeCameraViewWidthInCameraViewUnits)));


        // Create MeteringRectangle
        var sensorRect = new Microsoft.Maui.Graphics.Rect((sensorX - (sensorTouchAreaExtent / 2)),
             (sensorY - (sensorTouchAreaExtent / 2)),
             sensorTouchAreaExtent,
             sensorTouchAreaExtent);


        //_logger_LogTrace?.Invoke($"Converting from view coordinates too sensor coordinates");
        //_logger_LogTrace?.Invoke($"CameraView Size: {new Microsoft.Maui.Graphics.Size(this.Width, this.Height)} Sensor size: {new Microsoft.Maui.Graphics.Size(activeArraySize.Width(), activeArraySize.Height())}  ");
        //_logger_LogTrace?.Invoke($"CameraView Focus Rect: {rect} Sensor Focus Rect: {sensorRect}  ");


        var focusAreaTouch = new MeteringRectangle(
             (int)sensorRect.Left,
             (int)sensorRect.Top,
             (int)sensorRect.Width,
             (int)sensorRect.Height,
             MeteringRectangle.MeteringWeightMax
        );


//#if TAP_TO_FOCUS_CONTINIOUS
        previewBuilder.Set(CaptureRequest.ControlAfRegions, new MeteringRectangle[] { focusAreaTouch });
        previewSession.SetRepeatingRequest(previewBuilder.Build(), null, null);
//#else
//        //first stop the existing repeating request
//        previewSession.StopRepeating();

//        //cancel any existing AF trigger (repeated touches, etc.)
//        previewBuilder.Set(CaptureRequest.ControlAfTrigger, (int)ControlAFTrigger.Cancel);// CameraMetadata.Cance //CONTROL_AF_TRIGGER_CANCEL);
//        previewBuilder.Set(CaptureRequest.ControlAfMode, (int)ControlAFMode.Off);//CONTROL_AF_MODE_OFF);
//        var manualFocusEngagedCaptureCallback = new ManualFocusEngaged_CaptureCallback(this, _logger, _setFocusContext);
//        previewSession.Capture(previewBuilder.Build(), manualFocusEngagedCaptureCallback, null);


//        //Now add a new AF trigger with focus region
//        previewBuilder.Set(CaptureRequest.ControlMode, (int)ControlMode.Auto);  //.CONTROL_MODE, CameraMetadata.CONTROL_MODE_AUTO);
//        previewBuilder.Set(CaptureRequest.ControlAfMode, (int)ControlAFMode.Auto); //CONTROL_AF_MODE, CaptureRequest.CONTROL_AF_MODE_AUTO);
//        previewBuilder.Set(CaptureRequest.ControlAfTrigger, (int)ControlAFTrigger.Start); //CONTROL_AF_TRIGGER, CameraMetadata.CONTROL_AF_TRIGGER_START);
//        previewBuilder.SetTag("FOCUS_TAG"); //we'll capture this later for resuming the preview
//        previewBuilder.Set(CaptureRequest.ControlAfRegions, new MeteringRectangle[] { focusAreaTouch });
//        //then we ask for a single request (not repeating!)
//        previewSession.Capture(previewBuilder.Build(), manualFocusEngagedCaptureCallback, null);
//        ManualFocusEngaged = true;
//#endif
        return true;
    }


    private static Size ChooseMaxVideoSize(Size[] choices)
    {
        Size result = choices[0];
        int diference = 0;

        foreach (Size size in choices)
        {
            if (size.Width == size.Height * 4 / 3 && size.Width * size.Height > diference)
            {
                result = size;
                diference = size.Width * size.Height;
            }
        }

        return result;
    }
    private Size ChooseVideoSize(Size[] choices)
    {
        Size result = choices[0];
        int diference = int.MaxValue;
        bool swapped = IsDimensionSwapped();
        foreach (Size size in choices)
        {
            int w = swapped ? size.Height : size.Width;
            int h = swapped ? size.Width : size.Height;
            if (size.Width == size.Height * 4 / 3 && w >= Width && h >= Height && size.Width * size.Height < diference)
            {
                result = size;
                diference = size.Width * size.Height;
            }
        }

        return result;
    }

    /// <summary>
    /// Choose a resolution that matches the screen
    /// </summary>
    /// <param name="choices"></param>
    /// <returns></returns>
    private Size ChoosePreviewSize(Size[] choices)
    {
        return ChooseClosestMatch(choices, 
            new Size((int)DeviceDisplay.Current.MainDisplayInfo.Width, 
            (int)DeviceDisplay.Current.MainDisplayInfo.Height));
    }

    /// <summary>
    /// Return the resolution with the most pixels
    /// </summary>
    /// <param name="map"></param>
    /// <returns></returns>
    private Size ChoosePhotoResolution(StreamConfigurationMap map)
    {
        return ChooseMaximum(map.GetHighResolutionOutputSizes((int)ImageFormatType.Jpeg)
                      .Union(map.GetOutputSizes((int)ImageFormatType.Jpeg)));
    }

    private Size ChooseMaximum(IEnumerable<Size> choices)
    {
        return choices.OrderBy(x => x.Width * x.Height).Last();
    }

    private Size ChooseClosestMatch(IEnumerable<Size> choices, Size preferred)
    {
        Size bestChoice = choices.First();
        double bestDiff = double.MaxValue;

        foreach (Size size in choices)
        {
            // Calculate difference in aspect ratio and pixel count
            double aspectRatioDisplay = (double)preferred.Width / preferred.Height;
            double aspectRatioSize = (double)size.Width / size.Height;
            double aspectDiff = Math.Abs(aspectRatioDisplay - aspectRatioSize);

            // Prefer sizes that are not larger than the display
            int pixelDiff = Math.Abs((size.Width * size.Height) - (preferred.Width * preferred.Height));

            // Combine aspect and pixel diff for best match
            double diff = aspectDiff * 1000 + pixelDiff;

            if (diff < bestDiff)
            {
                bestDiff = diff;
                bestChoice = size;
            }
        }

        return bestChoice;
    }


    private void AdjustAspectRatio(int videoWidth, int videoHeight)
    {
        Matrix txform = new();
        /*
        float scaleX = (float)videoWidth / Width;
        float scaleY = (float)videoHeight / Height;
        bool swapped = IsDimensionSwapped();
        if (swapped)
        {
            scaleX = (float)videoHeight / Width;
            scaleY = (float)videoWidth / Height;
        }
        if (scaleX <= scaleY)
        {
            scaleY /= scaleX;
            scaleX = 1;
        }
        else
        {
            scaleX /= scaleY;
            scaleY = 1;
        }
        */
        RectF viewRect = new(0, 0, Width, Height);
        float centerX = viewRect.CenterX();
        float centerY = viewRect.CenterY();
        RectF bufferRect = new(0, 0, videoHeight, videoWidth);
        bufferRect.Offset(centerX - bufferRect.CenterX(), centerY - bufferRect.CenterY());
        txform.SetRectToRect(viewRect, bufferRect, Matrix.ScaleToFit.Fill);
        float scale = Math.Max(
                (float)Height / videoHeight,
                (float)Width / videoWidth);
        txform.PostScale(scale, scale, centerX, centerY);

        //txform.PostScale(scaleX, scaleY, centerX, centerY);
        IWindowManager windowManager = context.GetSystemService(Context.WindowService).JavaCast<IWindowManager>();
        var rotation = windowManager.DefaultDisplay.Rotation;
        if (SurfaceOrientation.Rotation90 == rotation || SurfaceOrientation.Rotation270 == rotation)
        {
            txform.PostRotate(90 * ((int)rotation - 2), centerX, centerY);
        }
        else if (SurfaceOrientation.Rotation180 == rotation)
        {
            txform.PostRotate(180, centerX, centerY);
        }
        textureView.SetTransform(txform);
    }

    protected override async void OnConfigurationChanged(Configuration newConfig)
    {
        base.OnConfigurationChanged(newConfig);
        if (started && !recording)
            await StartCameraAsync(cameraView.PhotosResolution);
    }

    private bool IsDimensionSwapped()
    {
        IWindowManager windowManager = context.GetSystemService(Context.WindowService).JavaCast<IWindowManager>();
        var displayRotation = windowManager.DefaultDisplay.Rotation;
        var chars = cameraManager.GetCameraCharacteristics(cameraView.Camera.DeviceId);
        int sensorOrientation = (int)(chars.Get(CameraCharacteristics.SensorOrientation) as Java.Lang.Integer);
        bool swappedDimensions = false;
        switch(displayRotation)
        {
            case SurfaceOrientation.Rotation0:
            case SurfaceOrientation.Rotation180:
                if (sensorOrientation == 90 || sensorOrientation == 270)
                {
                    swappedDimensions = true;
                }
                break;
            case SurfaceOrientation.Rotation90:
            case SurfaceOrientation.Rotation270:
                if (sensorOrientation == 0 || sensorOrientation == 180)
                {
                    swappedDimensions = true;
                }
                break;
        }
        return swappedDimensions;
    }

    private int GetJpegOrientation(int? deviceOrientation)
    {
        if (deviceOrientation == null)
        {
            IWindowManager windowManager = context.GetSystemService(Context.WindowService).JavaCast<IWindowManager>();
            var displayRotation = windowManager.DefaultDisplay.Rotation;
            deviceOrientation = displayRotation switch
            {
                SurfaceOrientation.Rotation90 => 90,
                SurfaceOrientation.Rotation180 => 180,
                SurfaceOrientation.Rotation270 => 270,
                _ => 0
            };
        }

        var chars = cameraManager.GetCameraCharacteristics(cameraView.Camera.DeviceId);
        int sensorOrientation = (int)(chars.Get(CameraCharacteristics.SensorOrientation) as Java.Lang.Integer);

        var cameraPosition = cameraView.Camera.Position == CameraPosition.Front ? -1 : 1;
        return (sensorOrientation - deviceOrientation.Value * cameraPosition + 360) % 360;
    }

    private class MyCameraStateCallback : CameraDevice.StateCallback
    {
        private readonly MauiCameraView cameraView;
        public MyCameraStateCallback(MauiCameraView camView)
        {
            cameraView = camView;
        }
        public override void OnOpened(CameraDevice camera)
        {
            if (camera != null)
            {
                cameraView.cameraDevice = camera;
                cameraView.StartPreview();
            }
        }

        public override void OnDisconnected(CameraDevice camera)
        {
            camera.Close();
            cameraView.cameraDevice = null;
        }

        public override void OnError(CameraDevice camera, CameraError error)
        {
            camera?.Close();
            cameraView.cameraDevice = null;
        }
    }

    private class PreviewCaptureStateCallback : CameraCaptureSession.StateCallback
    {
        private readonly MauiCameraView cameraView;
        public PreviewCaptureStateCallback(MauiCameraView camView)
        {
            cameraView = camView;
        }
        public override void OnConfigured(CameraCaptureSession session)
        {
            cameraView.previewSession = session;
            cameraView.UpdatePreview();

        }
        public override void OnConfigureFailed(CameraCaptureSession session)
        {
        }
    }
    class ImageAvailableListener : Java.Lang.Object, ImageReader.IOnImageAvailableListener
    {
        private readonly MauiCameraView cameraView;

        public ImageAvailableListener(MauiCameraView camView)
        {
            cameraView = camView;
        }
        public void OnImageAvailable(ImageReader reader)
        {
            try
            {
                var image = reader?.AcquireNextImage();
                if (image == null)
                    return;

                var buffer = image.GetPlanes()?[0].Buffer;
                if (buffer == null)
                    return;

                var imageData = new byte[buffer.Capacity()];
                buffer.Get(imageData);
                cameraView.capturePhoto = imageData;
                buffer.Clear();
                image.Close();
            }
            catch
            {
            }
            cameraView.captureDone = true;
        }
    }


}


