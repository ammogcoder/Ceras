﻿using System.Collections.Generic;

namespace Ceras.Helpers
{
	using Formatters;
	using System;
	using System.Diagnostics;
	using System.Linq.Expressions;
	using System.Reflection;
	using static System.Linq.Expressions.Expression;

	// todo 1: if we read the same block (containing a schema) twice, we need to recognize that it's the same and re-use the DynamicSchemaFormatter
	//			-> but that does not really happen, does it? every data-block contains only one schema per type.
	//			-> but what about serializing/deserializing multiple times?
	// todo 2: have a dictionary for known namespaces we write directly (without schema bc they never change)

	class SchemaDynamicFormatter<T> : IFormatter<T>, ISchemaTaintedFormatter
	{
		const int FieldSizePrefixBytes = 4;
		static readonly Type SizeType = typeof(uint);
		static readonly MethodInfo SizeWriteMethod = typeof(SerializerBinary).GetMethod(nameof(SerializerBinary.WriteUInt32Fixed));
		static readonly MethodInfo SizeReadMethod = typeof(SerializerBinary).GetMethod(nameof(SerializerBinary.ReadUInt32Fixed));


		// Instead of creating a new hash-set every time we need it, we just keep one around and re-use it.
		// This one will only ever get used during one function call
		static HashSet<Type> _tempHashSet = new HashSet<Type>();



		readonly CerasSerializer _ceras;

		readonly Dictionary<Schema, SerializerPair> _generatedSerializerPairs = new Dictionary<Schema, SerializerPair>();

		Schema _currentSchema;

		SerializeDelegate<T> _serializer;
		DeserializeDelegate<T> _deserializer;

		int _deserializationDepth; // recursion tracker for special types of schema-changes (can be removed eventually when we implemented a better solution)


		public SchemaDynamicFormatter(CerasSerializer ceras, Schema schema)
		{
			_ceras = ceras;
			_currentSchema = schema;

			var type = typeof(T);

			BannedTypes.ThrowIfBanned(type);
			BannedTypes.ThrowIfNonspecific(type);

			ActivateSchema(_currentSchema);

			RegisterForSchemaChanges();
		}


		public void Serialize(ref byte[] buffer, ref int offset, T value)
		{
			// _ceras.InstanceData.WrittenSchemata.Add(_schema);
			_serializer(ref buffer, ref offset, value);
		}

		public void Deserialize(byte[] buffer, ref int offset, ref T value)
		{
			try
			{
				_deserializationDepth++;
				_deserializer(buffer, ref offset, ref value);
			}
			finally
			{
				_deserializationDepth--;
			}
		}


		SerializeDelegate<T> GenerateSerializer(Schema schema)
		{
			var refBufferArg = Parameter(typeof(byte[]).MakeByRefType(), "buffer");
			var refOffsetArg = Parameter(typeof(int).MakeByRefType(), "offset");
			var valueArg = Parameter(typeof(T), "value");

			// todo: have a lookup list to directly get the actual 'SerializerBinary' method. There is no reason to actually use objects like "Int32Formatter"

			List<Expression> block = new List<Expression>();

			var startPos = Parameter(typeof(int), "startPos");
			var size = Parameter(typeof(int), "size");

			foreach (var schemaEntry in schema.Members)
			{
				if (schemaEntry.IsSkip)
					continue;

				var member = schemaEntry.Member;
				var type = member.MemberType;

				// Get Serialize method
				var formatter = _ceras.GetReferenceFormatter(type);
				var serializeMethod = formatter.GetType().GetMethod(nameof(IFormatter<int>.Serialize));
				Debug.Assert(serializeMethod != null, "Can't find serialize method on formatter " + formatter.GetType().FullName);

				// startPos = offset; 
				block.Add(Assign(startPos, refOffsetArg));

				// offset += 4;
				block.Add(AddAssign(refOffsetArg, Constant(FieldSizePrefixBytes)));

				// Serialize(...)
				block.Add(Call(
							   instance: Constant(formatter),
							   method: serializeMethod,
							   arg0: refBufferArg,
							   arg1: refOffsetArg,
							   arg2: MakeMemberAccess(valueArg, member.MemberInfo)
						  ));

				// size = (offset - startPos) - 4;
				block.Add(Assign(size, Subtract(Subtract(refOffsetArg, startPos), Constant(FieldSizePrefixBytes))));

				// offset = startPos;
				block.Add(Assign(refOffsetArg, startPos));

				// WriteInt32( size )
				block.Add(Call(
							   method: SizeWriteMethod,
							   arg0: refBufferArg,
							   arg1: refOffsetArg,
							   arg2: Convert(size, SizeType)
							   ));

				// offset = startPos + skipOffset;
				block.Add(Assign(refOffsetArg, Add(Add(startPos, size), Constant(FieldSizePrefixBytes))));

			}

			var serializeBlock = Block(variables: new[] { startPos, size }, expressions: block);

#if FAST_EXP
			return Expression.Lambda<SerializeDelegate<T>>(serializeBlock, refBufferArg, refOffsetArg, valueArg).CompileFast(true);
#else
			return Lambda<SerializeDelegate<T>>(serializeBlock, refBufferArg, refOffsetArg, valueArg).Compile();
#endif

		}

		DeserializeDelegate<T> GenerateDeserializer(Schema schema)
		{
			/*
			 * We got a schema (read from the data), and need to use it to read things in the right order
			 * and skip blocks that we want to skip
			 */

			var bufferArg = Parameter(typeof(byte[]), "buffer");
			var refOffsetArg = Parameter(typeof(int).MakeByRefType(), "offset");
			var refValueArg = Parameter(typeof(T).MakeByRefType(), "value");

			List<Expression> block = new List<Expression>();

			var blockSize = Variable(typeof(int), "blockSize");

			foreach (var sMember in schema.Members)
			{
				var member = sMember.Member;

				// 1. Read block size
				block.Add(Assign(left: blockSize,
								 right: Convert(Call(method: SizeReadMethod, arg0: bufferArg, arg1: refOffsetArg), typeof(int))));

				if (sMember.IsSkip)
				{
					// 2. a) Skip over the field
					block.Add(AddAssign(refOffsetArg, blockSize));
				}
				else
				{
					// 2. b) read normally
					var type = member.MemberType;
					IFormatter formatter = _ceras.GetReferenceFormatter(type);

					var deserializeMethod = formatter.GetType().GetMethod(nameof(IFormatter<int>.Deserialize));
					Debug.Assert(deserializeMethod != null, "Can't find deserialize method on formatter " + formatter.GetType().FullName);

					var fieldExp = MakeMemberAccess(refValueArg, member.MemberInfo);

					var serializeCall = Call(Constant(formatter), deserializeMethod, bufferArg, refOffsetArg, fieldExp);
					block.Add(serializeCall);
				}
			}

			var serializeBlock = Block(variables: new ParameterExpression[] { blockSize }, expressions: block);
#if FAST_EXP
			return Expression.Lambda<DeserializeDelegate<T>>(serializeBlock, bufferArg, refOffsetArg, refValueArg).CompileFast(true);
#else
			return Lambda<DeserializeDelegate<T>>(serializeBlock, bufferArg, refOffsetArg, refValueArg).Compile();
#endif

		}


		void RegisterForSchemaChanges()
		{
			if (_ceras.Config.VersionTolerance == VersionTolerance.Disabled)
				return;


			// We want to know when the schema of this type changes!
			_ceras.GetTypeMetaData(typeof(T)).OnSchemaChangeTargets.Add(this);

			// We also need to know about changes to value-type schemata.
			// But we have to ensure that we're recording ALL changes, not just the ones of the current schema (which might be missing entries!)
			var primarySchema = _ceras.SchemaDb.GetOrCreatePrimarySchema(typeof(T));

			foreach (var member in primarySchema.Members)
			{
				var memberType = member.Member.MemberType;
				
				// Only value-types are important, ref-types are handled somewhere else (ref-formatter)
				if(!memberType.IsValueType)
					continue;

				var memberMetaData = _ceras.GetTypeMetaData(member.Member.MemberType);
				memberMetaData.OnSchemaChangeTargets.Add(this);
			}



			// todo: this is for later, but probably we can avoid all this anyway if we select a better solution to the "inline problem"

			/*
			// What Schema changes do we want to know about?
			// When the schema of our own type or the schema of one of our members changes
			// 1.) Collect all types of whos schema we (so that when it changes, we know that we should update ourselves)
			_tempHashSet.Clear();

			_tempHashSet.Add(type);
			foreach (var m in schema.Members)
				_tempHashSet.Add(m.Member.MemberType);

			List<Schema> currentSchemata = new List<Schema>(_tempHashSet.Count);

			// 2.) Enter ourselves into the "interested" lists so that we get notified
			foreach (var t in _tempHashSet)
			{
				var meta = _ceras.GetTypeMetaData(t);
				meta.OnSchemaChangeTargets.Add(this);
				currentSchemata.Add(meta.CurrentSchema);
			}
			_tempHashSet.Clear();

			// 3.) Create a schema complex that represents the sum of all schemata we're currently using
			var currentSchemaComplex = new SchemaComplex(currentSchemata);
			*/

		}


		public void OnSchemaChanged(TypeMetaData meta)
		{
			// We're given the full metadata, but we only need the schema itself here
			// That simplifies the code because we can reuse the function for the constructor
			var schema = meta.CurrentSchema;
			ActivateSchema(schema);
		}

		void ActivateSchema(Schema schema)
		{
			// What schema changes are relevant to us?
			// - Schema of own type
			// - Schema of value-types inside us (dispatches for ref types are handled by RefFormatter anyway)


			// For now we only adapt to change to the current type schema.
			// Do we have serializers prepared for this schema already?

			if (_currentSchema == schema)
				return;


			// Important sanity check, if this happens the user should definitely know about it!
			if (_deserializationDepth > 0)
				if ()



					if (_generatedSerializerPairs.TryGetValue(schema, out var pair))
					{
						// Use already generated serializers 
						_serializer = pair.Serializer;
						_deserializer = pair.Deserializer;

						_currentSchema = schema;
						return;
					}

			// We have to make a new serializer pair
			if (schema.Members.Count == 0)
			{
				_serializer = (ref byte[] buffer, ref int offset, T value) => { };
				_deserializer = (byte[] buffer, ref int offset, ref T value) => { };
				return;
			}

			if (schema.IsPrimary)
			{
				_serializer = GenerateSerializer(schema);
				_deserializer = GenerateDeserializer(schema);
			}
			else
			{
				// No serializer! Writing data in some old format is not supported (yet, maybe in the future).
				// In theory we could do it. But it's not implemented because there would have to be some way for the user to specify what Schema to use.
				// And we get into all sorts of troubles with type-conversion (not implemented yet, but it will probably arrive earlier than this...)
				// This also protects us against bugs!
				_serializer = ErrorSerializer;
				_deserializer = GenerateDeserializer(schema);
			}

			_currentSchema = schema;

			_generatedSerializerPairs.Add(schema, new SerializerPair(_serializer, _deserializer));


			// todo: later we want to include the "schema complex" as well, which is the combination of multiple schemata.
			//       because that's the actual key here.
			//       but it makes things more difficult, it would most likely be better to chose one of the solutions below (in point #3)


			// 1) Mutate the current SchemaComplex and create a new SchemaComplex that represents the current one
			//    todo: how do we do this efficiently? We can't create a new SchemaComplex class and List<> just to (maybe) realize that we've already got one of those!


			// 2) After updating the current SchemaComplex, we check if we already got a serializer-pair that handles this, if not create a new one
			//    Then assign it.

			// 3) In case we're already deserializing while a schema change for a value-type appears, we've got a problem.
			//    First: Why do we not have a problem when its a reference-type?
			//    That's simply because we never call the formatter for those directly, we use the ReferenceFormatter, which will switch to the new formatter in time.
			//    Now for value-types that's a problem as the reference to the specific formatter is compiled as a constant into the formatter-delegate.
			//    
			//    -> Exception: All we can do is throw an exception here, warning the user that they're dealing with a very very strange situation.
			//    
			//    -> Force Ref: Force reference formatter in-between even for value-types; only to ensure that specific-formatters can be hot-swapped.
			//
			//    -> Virtualized Serialization: What we could do is have a setting that enforces "virtualized" deserialization, where nothing gets compiled and formatters are looked up constantly.
			//    Which is obviously extremely slow (on par with BinaryFormatter I guess), but the only solution. Should be fine, I can't imagine a real scenario where that isn't avoidable.
			//
			//    -> Cached Lookup: Instead of compiling it in as a constant, we could have an external "holder" object which we can change from the outside, and the delegate would always
			//    look into this object to get the refernce to the formatter. That way we could swap out the formatter even while we're already running it.
			//

		}


		static void ErrorSerializer(ref byte[] buffer, ref int offset, T value)
		{
			throw new InvalidOperationException("Trying to write using a non-primary ObjectSchema. This should never happen and is a bug, please report it on GitHub!");
		}


		struct SerializerPair
		{
			public readonly SerializeDelegate<T> Serializer;
			public readonly DeserializeDelegate<T> Deserializer;

			public SerializerPair(SerializeDelegate<T> serializer, DeserializeDelegate<T> deserializer)
			{
				Serializer = serializer;
				Deserializer = deserializer;
			}
		}
	}
}
