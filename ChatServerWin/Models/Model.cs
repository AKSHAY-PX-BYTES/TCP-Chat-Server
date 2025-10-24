using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ChatServerWin.Models
{
    
    public record Packet(string type, System.Text.Json.JsonElement payload);

}
