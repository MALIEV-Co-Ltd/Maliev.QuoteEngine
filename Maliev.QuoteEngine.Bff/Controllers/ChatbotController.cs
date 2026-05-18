using Asp.Versioning;
using Maliev.QuoteEngine.Bff.Services;
using Maliev.QuoteEngine.Shared.Chatbot;
using Microsoft.AspNetCore.Mvc;

namespace Maliev.QuoteEngine.Bff.Controllers;

/// <summary>
/// QuoteEngine customer-assistant boundary.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("quote/v{version:apiVersion}/chatbot")]
public sealed class ChatbotController(ICustomerChatbotService chatbotService) : ControllerBase
{
    /// <summary>
    /// Sends a customer message through the QuoteEngine BFF.
    /// </summary>
    [HttpPost("messages")]
    [ProducesResponseType(typeof(CustomerChatbotResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<CustomerChatbotResponse>> Send(
        [FromBody] CustomerChatbotRequest request,
        CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return ValidationProblem(ModelState);
        }

        return Ok(await chatbotService.SendAsync(request, cancellationToken));
    }

    /// <summary>
    /// Hydrates a signed customer-assistant handoff session.
    /// </summary>
    [HttpPost("hydrate")]
    [ProducesResponseType(typeof(CustomerChatbotHydrateResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<CustomerChatbotHydrateResponse>> Hydrate(
        [FromBody] CustomerChatbotHydrateRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await chatbotService.HydrateAsync(request, cancellationToken));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Invalid assistant handoff.",
                Detail = ex.Message
            });
        }
    }

    /// <summary>
    /// Gets the current QuoteEngine customer-assistant session state.
    /// </summary>
    [HttpGet("session")]
    [ProducesResponseType(typeof(CustomerChatbotSessionResponse), StatusCodes.Status200OK)]
    public ActionResult<CustomerChatbotSessionResponse> GetSession()
    {
        return Ok(chatbotService.GetSession());
    }
}
