using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.NineNow.Services;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.NineNow.Controllers;

/// <summary>Provides elevated dashboard actions for Nine device-code sign-in.</summary>
[ApiController]
[Route("NineNow/Auth")]
[Authorize(Policy = Policies.RequiresElevation)]
public sealed class NineNowAuthController : ControllerBase
{
    /// <summary>Gets the local sign-in state without exposing account tokens.</summary>
    [HttpGet("status")]
    public ActionResult<LoginStatus> Status()
    {
        NineNowPlugin? plugin = NineNowPlugin.Instance;
        if (plugin is null)
        {
            return Problem("The 9Now plugin is not initialized.", statusCode: 503);
        }

        return Ok(new LoginStatus(!string.IsNullOrWhiteSpace(plugin.Configuration.AccessToken)));
    }

    /// <summary>Starts a Nine device-code sign-in.</summary>
    [HttpPost("start")]
    public async Task<ActionResult<DeviceLoginStart>> Start(CancellationToken cancellationToken)
    {
        NineNowPlugin? plugin = NineNowPlugin.Instance;
        if (plugin is null)
        {
            return Problem("The 9Now plugin is not initialized.", statusCode: 503);
        }

        return Ok(await NineNowApiClient.StartDeviceLoginAsync(cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Polls Nine until the user approves the displayed activation code.</summary>
    [HttpPost("complete")]
    public async Task<ActionResult<LoginStatus>> Complete([FromBody] CompleteDeviceLoginRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        NineNowPlugin? plugin = NineNowPlugin.Instance;
        if (plugin is null)
        {
            return Problem("The 9Now plugin is not initialized.", statusCode: 503);
        }

        DeviceLoginCompletion completion = await NineNowApiClient.CompleteDeviceLoginAsync(
            plugin.Configuration,
            request.AuthCode,
            request.DeviceCode,
            cancellationToken).ConfigureAwait(false);
        if (completion.Complete)
        {
            plugin.SaveConfiguration(plugin.Configuration);
        }

        return Ok(new LoginStatus(completion.Complete || !string.IsNullOrWhiteSpace(plugin.Configuration.AccessToken), completion.Detail));
    }

    /// <summary>Removes the saved Nine account session.</summary>
    [HttpPost("logout")]
    public ActionResult<LoginStatus> Logout()
    {
        NineNowPlugin? plugin = NineNowPlugin.Instance;
        if (plugin is null)
        {
            return Problem("The 9Now plugin is not initialized.", statusCode: 503);
        }

        plugin.Configuration.AccessToken = string.Empty;
        plugin.Configuration.RefreshToken = string.Empty;
        plugin.Configuration.TokenExpiresAtUnixSeconds = 0;
        plugin.SaveConfiguration(plugin.Configuration);
        return Ok(new LoginStatus(false));
    }
}

/// <summary>Payload used to poll Nine device-code sign-in.</summary>
public sealed record CompleteDeviceLoginRequest(string AuthCode, string DeviceCode);

/// <summary>Whether a Nine account session has been saved.</summary>
public sealed record LoginStatus(bool Complete, string? Detail = null);
