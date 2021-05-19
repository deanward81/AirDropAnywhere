namespace AirDropAnywhere.Core.Extensibility
{
    public class AskResult
    {
        public AskResult(bool accepted)
        {
            Accepted = accepted;
        }
        
        public bool Accepted { get; }
    }
}