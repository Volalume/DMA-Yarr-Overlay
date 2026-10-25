using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using static Vortice.Direct3D11.D3D11;

internal static class Program
{
    private const int DxgiErrorWaitTimeout = unchecked((int)0x887A0027);

    [STAThread]
    private static int Main(string[] args)
    {
        Console.Title = "DXGI Output Duplication Capture";
        Console.WriteLine("============================================================");
        Console.WriteLine(" DXGI OUTPUT DUPLICATION CAPTURE");
        Console.WriteLine("============================================================");

        try
        {
            var requestedOutput = ParseOutputIndex(args);
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            var selected = SelectOutput(factory, requestedOutput);
            using var adapter = selected.Adapter;
            using var output = selected.Output;
            var description = output.Description;

            Console.WriteLine($"Display   : {description.DeviceName.TrimEnd('\0')}");
            Console.WriteLine($"Adapter   : {adapter.Description1.Description.TrimEnd('\0')}");
            Console.WriteLine($"Bounds    : {description.DesktopCoordinates}");
            Console.WriteLine($"Rotation  : {description.Rotation}");
            Console.WriteLine();
            Console.WriteLine("Waiting for the next desktop frame...");

            D3D11CreateDevice(
                adapter,
                DriverType.Unknown,
                DeviceCreationFlags.BgraSupport,
                new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1 },
                out ID3D11Device device,
                out _,
                out ID3D11DeviceContext context).CheckError();
            using (device)
            using (context)
            using (var output1 = output.QueryInterface<IDXGIOutput1>())
            using (var duplication = output1.DuplicateOutput(device))
            {
                var captured = CaptureFrame(device, context, duplication);
                var outputDirectory = Path.Combine(AppContext.BaseDirectory, "captures");
                Directory.CreateDirectory(outputDirectory);
                var path = Path.Combine(outputDirectory, $"dxgi_{DateTime.Now:yyyyMMdd_HHmmss}.png");
                using (captured.Bitmap)
                {
                    captured.Bitmap.Save(path, ImageFormat.Png);
                    Console.WriteLine();
                    Console.WriteLine($"Saved     : {path}");
                    Console.WriteLine($"Size      : {captured.Bitmap.Width}x{captured.Bitmap.Height}");
                    Console.WriteLine($"Protected : {(captured.ProtectedContentMaskedOut ? "MASKED OUT" : "not reported")}");
                    Console.WriteLine();
                    Console.WriteLine("Open the PNG and check whether the protected overlay is black or absent.");
                }
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine();
            Console.WriteLine($"FAILED: {ex.GetType().Name}: {ex.Message}");
            Console.ResetColor();
            return 1;
        }
        finally
        {
            Console.WriteLine();
            Console.WriteLine("Press Enter to close...");
            Console.ReadLine();
        }
    }

    private static int ParseOutputIndex(string[] args)
    {
        if (args.Length == 0) return 0;
        if (args.Length == 2 && args[0].Equals("--output", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(args[1], out var index) && index >= 0)
            return index;
        throw new ArgumentException("Usage: DxgiDuplicationCapture.exe [--output <zero-based-index>]");
    }

    private static (IDXGIAdapter1 Adapter, IDXGIOutput Output) SelectOutput(IDXGIFactory1 factory, int requestedIndex)
    {
        var current = 0;
        for (uint adapterIndex = 0; ; adapterIndex++)
        {
            var adapterResult = factory.EnumAdapters1(adapterIndex, out var adapter);
            if (adapterResult.Failure || adapter is null) break;
            for (uint outputIndex = 0; ; outputIndex++)
            {
                var outputResult = adapter.EnumOutputs(outputIndex, out var output);
                if (outputResult.Failure || output is null) break;
                if (current++ == requestedIndex) return (adapter, output);
                output.Dispose();
            }
            adapter.Dispose();
        }
        throw new ArgumentOutOfRangeException(nameof(requestedIndex), $"DXGI output index {requestedIndex} was not found.");
    }

    private static (Bitmap Bitmap, bool ProtectedContentMaskedOut) CaptureFrame(
        ID3D11Device device,
        ID3D11DeviceContext context,
        IDXGIOutputDuplication duplication)
    {
        IDXGIResource? resource = null;
        var ownsFrame = false;
        try
        {
            var protectedContentMaskedOut = false;
            for (var attempt = 0; attempt < 20; attempt++)
            {
                var result = duplication.AcquireNextFrame(500, out var frameInfo, out resource);
                if (result.Success)
                {
                    ownsFrame = true;
                    // AcquireNextFrame also wakes for hardware-pointer-only updates.
                    // LastPresentTime == 0 means there is no new desktop bitmap to
                    // copy, so release it and wait for a real DWM presentation.
                    if (frameInfo.LastPresentTime == 0)
                    {
                        resource?.Dispose();
                        resource = null;
                        duplication.ReleaseFrame().CheckError();
                        ownsFrame = false;
                        continue;
                    }
                    protectedContentMaskedOut = frameInfo.ProtectedContentMaskedOut;
                    break;
                }
                resource?.Dispose();
                resource = null;
                if (result.Code != DxgiErrorWaitTimeout) result.CheckError();
            }
            if (!ownsFrame || resource is null)
                throw new TimeoutException("No updated desktop bitmap arrived within ten seconds. Change something on the screen and retry.");

            using var source = resource.QueryInterface<ID3D11Texture2D>();
            var sourceDescription = source.Description;
            var stagingDescription = sourceDescription;
            stagingDescription.BindFlags = BindFlags.None;
            stagingDescription.Usage = ResourceUsage.Staging;
            stagingDescription.CPUAccessFlags = CpuAccessFlags.Read;
            stagingDescription.MiscFlags = ResourceOptionFlags.None;
            using var staging = device.CreateTexture2D(stagingDescription);
            context.CopyResource(staging, source);
            context.Flush();

            var mapResult = context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None, out var mapped);
            mapResult.CheckError();
            try
            {
                // Desktop Duplication returns BGRA pixels, but the desktop alpha
                // channel is not meaningful and is commonly zero. Store as BGRX
                // so PNG encoders do not interpret a valid desktop as transparent.
                var bitmap = new Bitmap((int)sourceDescription.Width, (int)sourceDescription.Height, PixelFormat.Format32bppRgb);
                CopyBgra(bitmap, mapped.DataPointer, (int)mapped.RowPitch);
                return (bitmap, protectedContentMaskedOut);
            }
            finally
            {
                context.Unmap(staging, 0);
            }
        }
        finally
        {
            resource?.Dispose();
            if (ownsFrame) duplication.ReleaseFrame();
        }
    }

    private static unsafe void CopyBgra(Bitmap bitmap, nint source, int sourcePitch)
    {
        var target = bitmap.LockBits(
            new Rectangle(0, 0, bitmap.Width, bitmap.Height),
            ImageLockMode.WriteOnly,
            PixelFormat.Format32bppRgb);
        try
        {
            var rowBytes = bitmap.Width * 4;
            for (var y = 0; y < bitmap.Height; y++)
                Buffer.MemoryCopy((byte*)source + y * sourcePitch, (byte*)target.Scan0 + y * target.Stride, rowBytes, rowBytes);
        }
        finally
        {
            bitmap.UnlockBits(target);
        }
    }
}
