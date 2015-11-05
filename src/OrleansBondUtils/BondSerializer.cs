﻿/*
Project Orleans Cloud Service SDK ver. 1.0
 
Copyright (c) Microsoft Corporation
 
All rights reserved.
 
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
associated documentation files (the ""Software""), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS
OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

namespace Orleans.Serialization
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Reflection;

    using Bond;
    using Runtime;

    using BondBinaryWriter = Bond.Protocols.SimpleBinaryWriter<Orleans.Serialization.OutputStream>;
    using BondTypeSerializer = Bond.Serializer<Bond.Protocols.SimpleBinaryWriter<Orleans.Serialization.OutputStream>>;
    using BondBinaryReader = Bond.Protocols.SimpleBinaryReader<Orleans.Serialization.InputStream>;
    using BondTypeDeserializer = Bond.Deserializer<Bond.Protocols.SimpleBinaryReader<Orleans.Serialization.InputStream>>;

    public class BondSerializer : IExternalSerializer
    {
        private static readonly ConcurrentDictionary<RuntimeTypeHandle, Delegate> CopierDictionary = new ConcurrentDictionary<RuntimeTypeHandle, Delegate>();
        private static readonly ConcurrentDictionary<RuntimeTypeHandle, BondTypeSerializer> SerializerDictionary = new ConcurrentDictionary<RuntimeTypeHandle, BondTypeSerializer>();
        private static readonly ConcurrentDictionary<RuntimeTypeHandle, BondTypeDeserializer> DeserializerDictionary = new ConcurrentDictionary<RuntimeTypeHandle, BondTypeDeserializer>();

        private TraceLogger logger;

        public bool IsSupportedType(Type itemType)
        {
            if (CopierDictionary.ContainsKey(itemType.TypeHandle))
            {
                return true;
            }

            if (itemType.IsGenericType && itemType.IsConstructedGenericType == false)
            {
                return false;
            }

            if (itemType.GetCustomAttribute<SchemaAttribute>() == null)
            {
                return false;
            }

            Register(itemType);
            return true;
        }

        public object DeepCopy(object source)
        {
            if (source == null)
            {
                return null;
            }

            var copier = GetCopier(source.GetType().TypeHandle);
            if (copier == null)
            {
                LogWarning(1, "no copier found for type {0}", source.GetType());
                throw new ArgumentOutOfRangeException("original", "no copier provided for the selected type");
            }

            return copier.DynamicInvoke(source);
        }

        public object Deserialize(Type expectedType, BinaryTokenStreamReader reader)
        {
            if (expectedType == null)
            {
                throw new ArgumentNullException("reader");
            }

            if (reader == null)
            {
                throw new ArgumentNullException("stream");
            }

            var typeHandle = expectedType.TypeHandle;
            var deserializer = GetDeserializer(typeHandle);
            if (deserializer == null)
            {
                LogWarning(3, "no deserializer found for type {0}", expectedType.FullName);
                throw new ArgumentOutOfRangeException("no deserializer provided for the selected type", "expectedType");
            }

            var inputStream = InputStream.Create(reader);
            var bondReader = new BondBinaryReader(inputStream);
            return deserializer.Deserialize(bondReader);
        }

        public void Initialize(TraceLogger logger)
        {
            this.logger = logger;
        }

        public void Serialize(object item, BinaryTokenStreamWriter writer, Type expectedType)
        {
            if (writer == null)
            {
                throw new ArgumentNullException("writer");
            }

            if (item == null)
            {
                writer.WriteNull();
                return;
            }

            var typeHandle = item.GetType().TypeHandle;
            var serializer = GetSerializer(typeHandle);
            if (serializer == null)
            {
                LogWarning(2, "no serializer found for type {0}", item.GetType());
                throw new ArgumentOutOfRangeException("no serializer provided for the selected type", "untypedInput");
            }

            var outputStream = OutputStream.Create(writer);
            var bondWriter = new BondBinaryWriter(outputStream);
            serializer.Serialize(item, bondWriter);
        }

        private static Delegate GetCopier(RuntimeTypeHandle handle)
        {
            return Get(CopierDictionary, handle);
        }

        private static BondTypeSerializer GetSerializer(RuntimeTypeHandle handle)
        {
            return Get(SerializerDictionary, handle);
        }

        private static BondTypeDeserializer GetDeserializer(RuntimeTypeHandle handle)
        {
            return Get(DeserializerDictionary, handle);
        }

        private static TValue Get<TValue>(IDictionary<RuntimeTypeHandle, TValue> dictionary, RuntimeTypeHandle key)
        {
            TValue value;
            dictionary.TryGetValue(key, out value);
            return value;
        }

        private void LogWarning(int code, Exception e, string format, params object[] parameters)
        {
            if (logger.IsWarning == false)
            {
                return;
            }

            logger.Warn(code, string.Format(format, parameters), e);
        }

        private void LogWarning(int code, string format, params object[] parameters)
        {
            if (logger.IsWarning == false)
            {
                return;
            }

            logger.Warn(code, format, parameters);
        }

        private void Register(Type type)
        {
            var clonerType = typeof(Cloner<>);
            var realType = clonerType.MakeGenericType(type);
            var clonerInstance = Activator.CreateInstance(realType);
            var cloneMethod = realType.GetMethod("Clone").MakeGenericMethod(type);
            var copierDelegate = cloneMethod.CreateDelegate(
                    typeof(Func<,>).MakeGenericType(new[] { type, type }),
                    clonerInstance);
            var serializer = new BondTypeSerializer(type);
            var deserializer = new BondTypeDeserializer(type);
            CopierDictionary.TryAdd(type.TypeHandle, copierDelegate);
            SerializerDictionary.TryAdd(type.TypeHandle, serializer);
            DeserializerDictionary.TryAdd(type.TypeHandle, deserializer);
        }
    }
}
