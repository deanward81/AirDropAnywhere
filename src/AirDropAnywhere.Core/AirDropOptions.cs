using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.FileProviders;

namespace AirDropAnywhere.Core
{
    public class AirDropOptions
    {
        [Required]
        public ushort ListenPort { get; set; }
        
        [Required]
        public string UploadPath { get; set; } = null!;
    }
}