using System.ComponentModel.DataAnnotations;

namespace AirDropAnywhere.Core
{
    public class AirDropOptions
    {
        [Required]
        public ushort ListenPort { get; set; }
    }
}