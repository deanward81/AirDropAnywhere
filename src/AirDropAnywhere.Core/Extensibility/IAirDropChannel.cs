using System.Threading.Tasks;

namespace AirDropAnywhere.Core.Extensibility
{
    // Receive
    // When a new client connects an instance of this class is registered with AirDropService
    // Registering will register a new mDNS entry with AirDropService
    //
    // When `Ask` or `Upload` is executed then AirDropService is notified 
    // and the relevant IAirDropChannel is pushed messages.
    //
    // Discover: 
    //
    // Ask: calls AskAsync, a blocking call that will generally display UX on the user's end
    //
    // if (_service.TryGetChannel(id, out var channel))
    // {
    //    var response = await channel.AskAsync();
    //    if (response.Accepted)
    //    {
    //        return Ok();
    //    }
    // }
    //
    // public class HubChannel
    // {
    //     ...
    // public ValueTask<AskResponse> AskAsync()
    // {
    //    // method call to client
    //    var response = await _hub.AskAsync();
    // }
    // }
    //
    
    public interface IAirDropChannel
    {
        /// <summary>
        /// Gets or sets the unique identifier of this channel.
        /// </summary>
        string Id { get; }
        
        /// <summary>
        /// Asks
        /// </summary>
        /// <returns></returns>
        ValueTask<AskResult> AskAsync();
    }
}