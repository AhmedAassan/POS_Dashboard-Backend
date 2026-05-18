namespace PosDashboard.Administration
{
    public interface ISMSService
    {
        /// <summary>
        /// Abstraction for an SMS service
        /// </summary>
        /// <param name="phoneNumber">Target phone number (should be validated)</param>
        /// <param name="text">Message text</param>
        /// <param name="reason">Sending reason for logging purposes</param>
        void Send(string phoneNumber, string text, string reason);
    }
}