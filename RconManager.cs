using System.Net;
using CoreRCON;

namespace ZomboidManager;

public class RconManager
{
    public async Task<string> SendCommandAsync(string host, int port, string password, string command)
    {
        RCON? rcon = null;
        try
        {
            if (!IPAddress.TryParse(host, out IPAddress? ipAddress))
                return $"RCON error: Invalid host address '{host}'.";

            rcon = new RCON(ipAddress, (ushort)port, password);
            await rcon.ConnectAsync();
            string response = await rcon.SendCommandAsync(command);
            return response;
        }
        catch (Exception ex)
        {
            return $"RCON error: {ex.Message}";
        }
        finally
        {
            rcon?.Dispose();
        }
    }
}
