using System;
using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.NineNow.Services;

/// <summary>Creates cached episode artwork with a visible DRM badge.</summary>
public sealed partial class DrmThumbnailRenderer
{
    private const int MaximumImageBytes = 12 * 1024 * 1024;
    private static readonly HttpClient HttpClient = new(new HttpClientHandler
    {
        AllowAutoRedirect = true,
        CheckCertificateRevocationList = true,
    });
    private readonly IApplicationPaths _applicationPaths;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly ILogger<DrmThumbnailRenderer> _logger;

    /// <summary>Initializes a new instance of the <see cref="DrmThumbnailRenderer"/> class.</summary>
    public DrmThumbnailRenderer(
        IApplicationPaths applicationPaths,
        IMediaEncoder mediaEncoder,
        ILogger<DrmThumbnailRenderer> logger)
    {
        _applicationPaths = applicationPaths;
        _mediaEncoder = mediaEncoder;
        _logger = logger;
    }

    /// <summary>Gets a local thumbnail path with a DRM badge, or null when rendering is unavailable.</summary>
    public async Task<string?> RenderAsync(Uri imageUri, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(imageUri);
        if (imageUri.Scheme != Uri.UriSchemeHttps
            || !IsAllowedImageHost(imageUri.Host)
            || string.IsNullOrWhiteSpace(_mediaEncoder.EncoderPath)
            || !_mediaEncoder.SupportsFilter("drawtext"))
        {
            return null;
        }

        string cacheDirectory = Path.Combine(_applicationPaths.ImageCachePath, "9Now", "DRM");
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(imageUri.AbsoluteUri)));
        string outputPath = Path.Combine(cacheDirectory, key + ".png");
        if (File.Exists(outputPath))
        {
            return outputPath;
        }

        Directory.CreateDirectory(cacheDirectory);
        string inputPath = Path.Combine(cacheDirectory, key + ".input");
        string temporaryOutputPath = Path.Combine(cacheDirectory, key + ".tmp.png");

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, imageUri);
            request.Headers.TryAddWithoutValidation("User-Agent", NineNowApiClient.StreamUserAgent);
            using HttpResponseMessage response = await HttpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            Uri? finalUri = response.RequestMessage?.RequestUri;
            if (finalUri is null || finalUri.Scheme != Uri.UriSchemeHttps || !IsAllowedImageHost(finalUri.Host))
            {
                return null;
            }

            if (response.Content.Headers.ContentLength is > MaximumImageBytes)
            {
                return null;
            }

            Stream source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using (source.ConfigureAwait(false))
            using (var destination = new MemoryStream())
            {
                byte[] buffer = new byte[81920];
                int totalBytes = 0;
                int bytesRead;
                while ((bytesRead = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    totalBytes += bytesRead;
                    if (totalBytes > MaximumImageBytes)
                    {
                        return null;
                    }

                    await destination.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken).ConfigureAwait(false);
                }

                await File.WriteAllBytesAsync(inputPath, destination.ToArray(), cancellationToken).ConfigureAwait(false);
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            using var process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = _mediaEncoder.EncoderPath,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                },
            };

            process.StartInfo.ArgumentList.Add("-hide_banner");
            process.StartInfo.ArgumentList.Add("-loglevel");
            process.StartInfo.ArgumentList.Add("error");
            process.StartInfo.ArgumentList.Add("-nostdin");
            process.StartInfo.ArgumentList.Add("-y");
            process.StartInfo.ArgumentList.Add("-i");
            process.StartInfo.ArgumentList.Add(inputPath);
            process.StartInfo.ArgumentList.Add("-vf");
            process.StartInfo.ArgumentList.Add(
                "drawbox=x=iw*0.035:y=ih*0.035:w=iw*0.30:h=ih*0.18:color=0xB00020@0.94:t=fill," +
                "drawtext=font=Sans:text=DRM:fontcolor=white:fontsize=iw/18:x=iw*0.07:y=ih*0.065");
            process.StartInfo.ArgumentList.Add("-frames:v");
            process.StartInfo.ArgumentList.Add("1");
            process.StartInfo.ArgumentList.Add(temporaryOutputPath);

            if (!process.Start())
            {
                return null;
            }

            Task<string> errorOutput = process.StandardError.ReadToEndAsync(CancellationToken.None);
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                process.Kill(true);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                return null;
            }

            string errors = await errorOutput.ConfigureAwait(false);
            if (process.ExitCode != 0 || !File.Exists(temporaryOutputPath))
            {
                LogRenderFailed(_logger, errors);
                return null;
            }

            File.Move(temporaryOutputPath, outputPath, true);
            return outputPath;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException
            or HttpRequestException
            or UnauthorizedAccessException
            or InvalidOperationException
            or Win32Exception
            or NotSupportedException
            or ArgumentException
            or SecurityException
            or TaskCanceledException)
        {
            LogRenderFailed(_logger, ex);
            return null;
        }
        finally
        {
            TryDelete(inputPath);
            TryDelete(temporaryOutputPath);
        }
    }

    private static bool IsAllowedImageHost(string host)
    {
        return host.Equals("static9.net.au", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".static9.net.au", StringComparison.OrdinalIgnoreCase)
            || host.Equals("9now.com.au", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".9now.com.au", StringComparison.OrdinalIgnoreCase)
            || host.Equals("nine.com.au", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".nine.com.au", StringComparison.OrdinalIgnoreCase)
            || host.Equals("vms-tv-images-prod.s3-ap-southeast-2.amazonaws.com", StringComparison.OrdinalIgnoreCase);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Could not render a DRM badge for a 9Now thumbnail: {Error}")]
    private static partial void LogRenderFailed(ILogger logger, string error);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Could not render a DRM badge for a 9Now thumbnail.")]
    private static partial void LogRenderFailed(ILogger logger, Exception exception);
}
