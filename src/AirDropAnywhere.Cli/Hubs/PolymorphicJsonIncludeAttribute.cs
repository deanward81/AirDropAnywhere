using System;

namespace AirDropAnywhere.Cli.Hubs
{
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
    internal class PolymorphicJsonIncludeAttribute : Attribute
    {
        public PolymorphicJsonIncludeAttribute(string name, Type type)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            Type = type ?? throw new ArgumentNullException(nameof(type));
        }
    
        /// <summary>
        /// Gets the name of the mapping when serialized by the <see cref="PolymorphicJsonConverter"/>.
        /// </summary>
        public string Name { get; }
        
        /// <summary>
        /// Gets the <see cref="Type"/> that should be handled by the class.
        /// </summary>
        public Type Type { get; }
    }
}