﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml;
using System.Xml.Serialization;
using Rock.DependencyInjection;

// ReSharper disable once CheckNamespace
namespace Rock.Configuration
{
    public abstract class XmlDeserializingFactory<T>
    {
        private readonly Type _defaultType;
        private Lazy<Type> _type;

        protected XmlDeserializingFactory(Type defaultType)
        {
            _defaultType = ThrowIfNotAssignableToT(defaultType);
            _type = new Lazy<Type>(() => _defaultType);
        }

        [XmlAttribute("type")]
        public string TypeAssemblyQualifiedName
        {
            get { return _type.Value.AssemblyQualifiedName; }
            set { _type = new Lazy<Type>(() => value != null ? ThrowIfNotAssignableToT(Type.GetType(value)) : _defaultType); }
        }

        [XmlAnyAttribute]
        public XmlAttribute[] AdditionalAttributes { get; set; }

        [XmlAnyElement]
        public XmlElement[] AdditionalElements { get; set; }

        public T CreateInstance()
        {
            return CreateInstance(null);
        }

        public virtual T CreateInstance(IResolver resolver)
        {
            if (_type.Value == null)
            {
                throw new InvalidOperationException("A value for 'type' must provided - no default value exists.");
            }

            var creationScenario = GetCreationScenario();
            var args = CreateArgs(creationScenario.Parameters, resolver);
            var instance = creationScenario.Constructor.Invoke(args);

            foreach (var property in creationScenario.Properties)
            {
                object value;

                if (TryGetValueForInstance(property.Name, property.PropertyType, out value))
                {
                    property.SetValue(instance, value);
                }
            }

            return (T)instance;
        }

        private CreationScenario GetCreationScenario()
        {
            return new CreationScenario(GetConstructor(), _type.Value);
        }

        private ConstructorInfo GetConstructor()
        {
            return _type.Value.GetConstructors().OrderByDescending(c => c.GetParameters().Length).First();
        }

        private object[] CreateArgs(IEnumerable<ParameterInfo> parameters, IResolver resolver)
        {
            var argsList = new List<object>();

            foreach (var parameter in parameters)
            {
                object argValue;

                if (TryGetValueForInstance(parameter.Name, parameter.ParameterType, out argValue))
                {
                    argsList.Add(argValue);
                }
                else if (resolver != null && resolver.CanGet(parameter.ParameterType))
                {
                    argsList.Add(resolver.Get(parameter.ParameterType));
                }
                else
                {
                    argsList.Add(parameter.HasDefaultValue ? parameter.DefaultValue : null);
                }
            }

            return argsList.ToArray();
        }

        private bool TryGetValueForInstance(string name, Type type, out object value)
        {
            if (TryGetPropertyValue(name, type, out value))
            {
                if (value == null)
                {
                    object additionalValue;
                    if (TryGetAdditionalValue(name, type, out additionalValue))
                    {
                        value = additionalValue;
                    }
                }

                return true;
            }

            if (TryGetAdditionalValue(name, type, out value))
            {
                return true;
            }

            return false;
        }

        private bool TryGetPropertyValue(string name, Type type, out object value)
        {
            var properties =
                GetType().GetProperties()
                    .Where(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && p.PropertyType == type)
                    .OrderBy(p => p.Name, new CaseSensitiveEqualityFirstAsComparedTo(name));

            foreach (var property in properties)
            {
                // ReSharper disable once EmptyGeneralCatchClause
                try
                {
                    value = property.GetValue(this);
                    return true;
                }
                catch
                {
                }
            }

            value = null;
            return false;
        }

        private bool TryGetAdditionalValue(string name, Type type, out object value)
        {
            foreach (var additionalNode in GetAdditionalNodes(name))
            {
                var additionalElement = additionalNode as XmlElement;

                if (additionalElement != null)
                {
                    if (TryGetElementValue(additionalElement, type, out value))
                    {
                        return true;
                    }
                }
                else
                {
                    var additionalAttribute = (XmlAttribute)additionalNode;

                    if (TryConvert(additionalAttribute.Value, type, out value))
                    {
                        return true;
                    }
                }
            }

            value = null;
            return false;
        }

        private IEnumerable<XmlNode> GetAdditionalNodes(string name)
        {
            var allAdditionalNodes =
                (AdditionalElements ?? Enumerable.Empty<XmlNode>())
                    .Concat(AdditionalAttributes ?? Enumerable.Empty<XmlNode>());

            var additionalNodes = allAdditionalNodes
                .Where(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                .OrderBy(x => x.Name, new CaseSensitiveEqualityFirstAsComparedTo(name))
                .ThenBy(x => x, new ElementsBeforeAttributes());

            return additionalNodes;
        }

        private static bool TryGetElementValue(XmlElement additionalElement, Type type, out object value)
        {
            if (!additionalElement.HasChildNodes && !additionalElement.HasAttributes)
            {
                if (TryConvert(additionalElement.Value, type, out value))
                {
                    return true;
                }
            }

            using (var reader = new StringReader(additionalElement.OuterXml))
            {
                try
                {
                    XmlSerializer serializer;

                    if (type.IsInterface || type.IsAbstract)
                    {
                        var typeName = additionalElement.GetAttribute("type");
                        var typeFromAttribute = Type.GetType(typeName);

                        if (typeFromAttribute == null)
                        {
                            value = null;
                            return false;
                        }

                        serializer = new XmlSerializer(typeFromAttribute, new XmlRootAttribute(additionalElement.Name));
                    }
                    else
                    {
                        serializer = new XmlSerializer(type, new XmlRootAttribute(additionalElement.Name));
                    }

                    value = serializer.Deserialize(reader);
                    return true;
                }
                catch
                {
                    value = null;
                    return false;
                }
            }
        }

        private static bool TryConvert(string stringValue, Type type, out object value)
        {
            var converter = TypeDescriptor.GetConverter(type);

            if (converter.CanConvertFrom(typeof(string)))
            {
                value = converter.ConvertFrom(stringValue);
                return true;
            }

            converter = TypeDescriptor.GetConverter(typeof(string));

            if (converter.CanConvertTo(type))
            {
                value = converter.ConvertTo(stringValue, type);
                return true;
            }

            value = null;
            return false;
        }

        private static Type ThrowIfNotAssignableToT(Type type)
        {
            if (type == null)
            {
                return null;
            }

            if (!typeof(T).IsAssignableFrom(type))
            {
                throw new ArgumentException(string.Format("The provided Type, {0}, must be assignable to Type {1}.", type, typeof(T)));
            }

            return type;
        }

        private class CreationScenario
        {
            private readonly ConstructorInfo _ctor;
            private readonly ParameterInfo[] _parameters;
            private readonly IEnumerable<PropertyInfo> _properties;

            public CreationScenario(ConstructorInfo ctor, Type type)
            {
                _ctor = ctor;
                _parameters = ctor.GetParameters();

                var parameterNames = _parameters.Select(p => p.Name).ToList();

                _properties =
                    type.GetProperties()
                        .Where(p =>
                            p.CanRead
                            && p.CanWrite
                            && p.GetGetMethod().IsPublic
                            && p.GetSetMethod().IsPublic
                            && parameterNames.All(parameterName => !parameterName.Equals(p.Name, StringComparison.OrdinalIgnoreCase)));
            }

            public ConstructorInfo Constructor { get { return _ctor; } }
            public IEnumerable<ParameterInfo> Parameters { get { return _parameters; } }
            public IEnumerable<PropertyInfo> Properties { get { return _properties; } }
        }

        private class CaseSensitiveEqualityFirstAsComparedTo : IComparer<string>
        {
            private readonly string _nameToMatch;

            public CaseSensitiveEqualityFirstAsComparedTo(string nameToMatch)
            {
                _nameToMatch = nameToMatch;
            }

            public int Compare(string lhs, string rhs)
            {
                if (string.Equals(lhs, rhs, StringComparison.Ordinal))
                {
                    return 0;
                }

                if (string.Equals(lhs, _nameToMatch, StringComparison.Ordinal))
                {
                    return -1;
                }

                if (string.Equals(rhs, _nameToMatch, StringComparison.Ordinal))
                {
                    return 1;
                }

                return 0;
            }
        }

        private class ElementsBeforeAttributes : IComparer<XmlNode>
        {
            public int Compare(XmlNode lhs, XmlNode rhs)
            {
                if (lhs is XmlElement)
                {
                    return (rhs is XmlAttribute) ? -1 : 0;
                }

                return (rhs is XmlElement) ? 1 : 0;
            }
        }
    }
}