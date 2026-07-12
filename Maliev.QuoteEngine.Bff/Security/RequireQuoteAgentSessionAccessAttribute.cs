using Maliev.QuoteEngine.Bff.Services;
using Maliev.QuoteEngine.Shared.Agent;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;

namespace Maliev.QuoteEngine.Bff.Security;

/// <summary>How an agent-session endpoint may resolve its session owner.</summary>
public enum QuoteAgentSessionAccessMode
{
    /// <summary>The session must already have a durable owner.</summary>
    Existing,

    /// <summary>A new, pristine session may be atomically bound to the current browser principal.</summary>
    CreateOrResume
}

/// <summary>
/// Enforces the durable customer-or-visitor owner on an AgentController action before the action or
/// any downstream service call runs.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class RequireQuoteAgentSessionAccessAttribute(QuoteAgentSessionAccessMode mode)
    : Attribute, IAsyncResourceFilter, IAsyncActionFilter
{
    private static readonly object AuthorizedSessionKey = new();

    /// <summary>Gets the access mode declared by the endpoint.</summary>
    public QuoteAgentSessionAccessMode Mode { get; } = mode;

    /// <inheritdoc />
    public async Task OnResourceExecutionAsync(
        ResourceExecutingContext context,
        ResourceExecutionDelegate next)
    {
        if (!TryResolveRouteSessionId(context.RouteData.Values, out var sessionId))
        {
            await next();
            return;
        }

        var result = await AuthorizeAsync(context.HttpContext, sessionId);
        if (result is not null)
        {
            DisableStatusCodeBody(context.HttpContext);
            context.Result = result;
            return;
        }

        context.HttpContext.Items[AuthorizedSessionKey] = sessionId;
        await next();
    }

    /// <inheritdoc />
    public async Task OnActionExecutionAsync(
        ActionExecutingContext context,
        ActionExecutionDelegate next)
    {
        var sessionId = ResolveSessionId(context);
        if (!sessionId.HasValue || sessionId == Guid.Empty)
        {
            DisableStatusCodeBody(context.HttpContext);
            context.Result = new EmptyStatusCodeResult(StatusCodes.Status404NotFound);
            return;
        }

        var alreadyAuthorized =
            context.HttpContext.Items.TryGetValue(AuthorizedSessionKey, out var authorized) &&
            authorized is Guid authorizedSessionId &&
            authorizedSessionId == sessionId.Value;
        if (!alreadyAuthorized)
        {
            context.Result = await AuthorizeAsync(context.HttpContext, sessionId.Value);
            if (context.Result is not null)
            {
                DisableStatusCodeBody(context.HttpContext);
                return;
            }
        }

        if (!TryAuthorizeAttachments(context, sessionId.Value))
        {
            DisableStatusCodeBody(context.HttpContext);
            context.Result = new EmptyStatusCodeResult(StatusCodes.Status404NotFound);
            return;
        }

        await next();
    }

    private Guid? ResolveSessionId(ActionExecutingContext context)
    {
        if (context.ActionArguments.TryGetValue("sessionId", out var routeSession) &&
            routeSession is Guid sessionId)
        {
            return sessionId;
        }

        foreach (var argument in context.ActionArguments.Values)
        {
            if (argument is QuoteAgentMessageRequest message)
            {
                if ((!message.SessionId.HasValue || message.SessionId == Guid.Empty) &&
                    Mode == QuoteAgentSessionAccessMode.CreateOrResume)
                {
                    message.SessionId = Guid.NewGuid();
                }

                return message.SessionId;
            }

            if (argument is QuoteAgentExportPdfRequest export)
            {
                return export.SessionId;
            }
        }

        return null;
    }

    private async Task<IActionResult?> AuthorizeAsync(HttpContext context, Guid sessionId)
    {
        var access = context.RequestServices.GetRequiredService<QuoteAgentSessionAccess>();
        var decision = await access.AuthorizeAsync(
            sessionId,
            Mode == QuoteAgentSessionAccessMode.CreateOrResume,
            context.RequestAborted);
        return decision switch
        {
            QuoteAgentSessionAccessDecision.Authorized => null,
            QuoteAgentSessionAccessDecision.Unauthorized => new EmptyStatusCodeResult(StatusCodes.Status401Unauthorized),
            _ => new EmptyStatusCodeResult(StatusCodes.Status404NotFound)
        };
    }

    private static bool TryAuthorizeAttachments(ActionExecutingContext context, Guid sessionId)
    {
        var attachmentAccess = context.HttpContext.RequestServices
            .GetRequiredService<QuoteAgentAttachmentAccess>();
        foreach (var argument in context.ActionArguments.Values)
        {
            switch (argument)
            {
                case QuoteAgentMessageRequest message:
                    if (!attachmentAccess.TryAuthorizeAndCanonicalize(
                            sessionId,
                            message.Attachments,
                            out var messageAttachments))
                    {
                        return false;
                    }

                    message.Attachments = messageAttachments;
                    break;
                case QuoteAgentAttachmentRegisterRequest registration:
                    if (!attachmentAccess.TryAuthorizeAndCanonicalize(
                            sessionId,
                            registration.Attachments,
                            out var registeredAttachments))
                    {
                        return false;
                    }

                    registration.Attachments = registeredAttachments;
                    break;
            }
        }

        return true;
    }

    private static bool TryResolveRouteSessionId(
        RouteValueDictionary routeValues,
        out Guid sessionId)
    {
        sessionId = Guid.Empty;
        if (!routeValues.TryGetValue("sessionId", out var rawSessionId))
        {
            return false;
        }

        return rawSessionId is Guid parsedSessionId
            ? (sessionId = parsedSessionId) != Guid.Empty
            : Guid.TryParse(Convert.ToString(rawSessionId), out sessionId) && sessionId != Guid.Empty;
    }

    private static void DisableStatusCodeBody(HttpContext context)
    {
        var statusCodePages = context.Features.Get<IStatusCodePagesFeature>();
        if (statusCodePages is not null)
        {
            statusCodePages.Enabled = false;
        }
    }

    private sealed class EmptyStatusCodeResult(int statusCode) : IActionResult
    {
        public Task ExecuteResultAsync(ActionContext context)
        {
            context.HttpContext.Response.StatusCode = statusCode;
            return Task.CompletedTask;
        }
    }
}
