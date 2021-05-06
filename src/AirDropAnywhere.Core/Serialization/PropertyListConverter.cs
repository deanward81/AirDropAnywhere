using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;
using Claunia.PropertyList;

namespace AirDropAnywhere.Core.Serialization
{
    /// <summary>
    /// Helper class for converting between .NET types and the <see cref="NSObject"/>
    /// type hierarchy used by plist-cil.
    /// </summary>
    internal static class PropertyListConverter
    {
        private const BindingFlags PropertyFlags = BindingFlags.Instance | BindingFlags.Public;
        
        // ReSharper disable once InconsistentNaming
        public static NSObject? ToNSObject(object? obj)
        {
            if (obj == null)
            {
                return null;
            }
            
            var type = obj.GetType();
            if (type.IsPrimitive || type == typeof(string) || type == typeof(byte[]))
            {
                // NSObject can deal with this itself
                return NSObject.Wrap(obj);
            }
            
            if (IsSet(type))
            {
                var nsSet = new NSSet();
                foreach (var value in (IEnumerable) obj)
                {
                    nsSet.AddObject(ToNSObject(value));
                }
                return nsSet;
            }

            if (IsDictionary(type))
            {
                var nsDictionary = new NSDictionary();
                foreach (DictionaryEntry kvp in (IDictionary) obj)
                {
                    nsDictionary.Add((string)kvp.Key, ToNSObject(kvp.Value));
                }
                return nsDictionary;
            }
            
            if (IsEnumerable(type))
            {
                var nsArray = new NSArray();
                foreach (var value in (IEnumerable) obj)
                {
                    nsArray.Add(ToNSObject(value));
                }
                return nsArray;    
            }
            
            var dict = new NSDictionary();
            foreach (var property in type.GetProperties(PropertyFlags))
            {
                var name = property.Name;
                var dataMemberAttr = property.GetCustomAttribute<DataMemberAttribute>();
                if (dataMemberAttr?.Name != null)
                {
                    name = dataMemberAttr.Name;
                }

                dict.Add(name, ToNSObject(property.GetValue(obj)));
            }
            return dict;
        }

        /// <summary>
        /// Converts an <see cref="NSObject"/> into the specified type <typeparamref name="T"/>. It uses reflection to inspect
        /// the properties on <typeparamref name="T"/> and materializes
        /// an instance of the type, populated with the contents of an <see cref="NSDictionary"/>. 
        /// </summary>
        public static T ToObject<T>(NSObject root) => (T)ToObject(root, typeof(T));

        /// <summary>
        /// Helper method that converts an <see cref="NSObject"/> into
        /// the specified <see cref="Type"/>. It uses reflection to inspect
        /// the properties on <paramref name="type"/> and materializes
        /// an instance of the type, populated with the contents of an <see cref="NSDictionary"/>. 
        /// </summary>
        public static object ToObject(NSObject root, Type type)
        {
            InvalidCastException InvalidType() => new(
                $"Unable to bind '{root.GetType()}' to collection type '{type}'"
            );

            if (type == typeof(byte[]) && root is NSData nsData)
            {
                return nsData.Bytes;
            }

            if (root is NSSet nsSet)
            {
                var elementType = GetElementType(type);
                if (elementType == null)
                {
                    throw InvalidType();
                }
                
                var set = (IList) Activator.CreateInstance(typeof(HashSet<>).MakeGenericType(elementType))!;
                foreach (NSObject nsObject in nsSet)
                {
                    set.Add(ToObject(nsObject, elementType));
                }

                return set;
            }
            
            if (root is NSArray nsArray)
            {
                var elementType = GetElementType(type);
                if (elementType == null)
                {
                    throw InvalidType();
                }
                
                var list = (IList) Activator.CreateInstance(typeof(List<>).MakeGenericType(elementType))!;
                for (int i = 0; i < nsArray.Count; i++)
                {
                    list.Add(ToObject(nsArray[i], elementType));
                }

                return list;
            }
            
            if (root is NSNumber nsNumber)
            {
                if (type == typeof(bool))
                {
                    return nsNumber.ToBool();
                }

                if (type == typeof(double))
                {
                    return nsNumber.ToDouble();
                }

                if (type == typeof(float))
                {
                    return nsNumber.floatValue();
                }

                if (type == typeof(int))
                {
                    return nsNumber.ToInt();
                }

                if (type == typeof(long))
                {
                    return nsNumber.ToLong();
                }

                throw InvalidType();
            }

            if (root is NSString nsString)
            {
                if (type == typeof(string))
                {
                    return nsString.Content;
                }
                
                throw InvalidType();
            }

            if (root is NSDate nsDate)
            {
                if (type == typeof(DateTime))
                {
                    return nsDate.Date;
                }

                throw InvalidType();
            }

            if (root is NSDictionary nsDictionary)
            {
                if (type.IsPrimitive)
                {
                    // can't convert a dictionary to a primitive type
                    throw InvalidType();
                }

                var elementType = GetDictionaryValueType(type);
                if (elementType != null)
                {
                    var dict = (IDictionary) Activator.CreateInstance(
                        typeof(Dictionary<,>).MakeGenericType(typeof(string), elementType)
                    )!;
                    foreach (var kvp in nsDictionary)
                    {
                        dict.Add(kvp.Key, ToObject(kvp.Value, elementType));
                    }

                    return dict;
                }
                
                // construct an object that we can use
                // and populate its properties
                var instance = Activator.CreateInstance(type)!;
                foreach (var property in type.GetProperties(PropertyFlags))
                {
                    if (!property.CanWrite)
                    {
                        continue;
                    }
                    
                    var name = property.Name;
                    var dataMemberAttr = property.GetCustomAttribute<DataMemberAttribute>();
                    if (dataMemberAttr?.Name != null)
                    {
                        name = dataMemberAttr.Name;
                    }
                    
                    if (nsDictionary.TryGetValue(name, out var nsObject))
                    {
                        property.SetValue(instance, ToObject(nsObject, property.PropertyType));
                    }
                }

                return instance;
            }

            throw InvalidType();
        }

        private static Type? GetDictionaryValueType(Type type)
        {
            static bool IsDictionaryType(Type type) => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IDictionary<,>);

            if (IsDictionaryType(type))
            {
                return type.GetGenericArguments()[1];
            }
            
            foreach (var interfaceType in type.GetInterfaces())
            {
                if (IsDictionaryType(interfaceType))
                {
                    return interfaceType.GetGenericArguments()[1];
                }
            }

            if (type.GetInterface(nameof(IDictionary)) != null)
            {
                return typeof(object);
            }

            return null;
        }

        private static bool IsDictionary(Type type)
        {
            if (HasInterface(type, typeof(IDictionary)))
            {
                return true;
            }

            return HasInterface(type, typeof(IDictionary<,>));
        }

        private static bool IsSet(Type type) => HasInterface(type, typeof(ISet<>));
        
        private static bool IsEnumerable(Type type)
        {
            if (type == typeof(string))
            {
                // strings are IEnumerable<char> but we don't want
                // to treat them that way!
                return false;
            }
            
            if (type.IsArray)
            {
                return true;
            }

            if (HasInterface(type, typeof(IEnumerable)))
            {
                return true;
            }

            return HasInterface(type, typeof(IEnumerable<>));
        }
        
        private static Type? GetElementType(Type type)
        {
            if (type == typeof(string))
            {
                return null;
            }
            
            if (type.IsArray)
            {
                return type.GetElementType();
            }
        
            static bool IsEnumerableType(Type type) => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IEnumerable<>);
            
            if (IsEnumerableType(type))
            {
                return type.GetGenericArguments()[0];
            }

            foreach (var interfaceType in type.GetInterfaces())
            {
                if (IsEnumerableType(interfaceType))
                {
                    return interfaceType.GetGenericArguments()[0];
                }
            }

            if (type.GetInterface(nameof(IEnumerable)) != null)
            {
                return typeof(object);
            }

            return null;
        }
        
        private static bool HasInterface(Type type, Type interfaceType)
        {
            if (type == null)
            {
                throw new ArgumentNullException(nameof(type));
            }

            if (interfaceType == null)
            {
                throw new ArgumentNullException(nameof(interfaceType));
            }

            if (!interfaceType.IsInterface)
            {
                throw new ArgumentException("Must be an interface type", nameof(interfaceType));
            }

            bool ImplementsInterface(Type typeToCheck)
            {
                if (interfaceType.IsGenericTypeDefinition)
                {
                    return typeToCheck.IsGenericType && typeToCheck.GetGenericTypeDefinition() == interfaceType;
                }

                return typeToCheck == interfaceType;
            }
            
            if (ImplementsInterface(type))
            {
                return true;
            }

            foreach (var implementedType in type.GetInterfaces())
            {
                if (ImplementsInterface(implementedType))
                {
                    return true;
                }
            }

            return false;
        }
    }
}