﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using PListNet.Exceptions;
using PListNet.Primitives;
using PListNet.Collections;
using System.Linq;

namespace PListNet.Internal
{
	/// <summary>
	/// A class, used to write a <see cref="T:PListNet.PNode"/>  binary formated to a stream
	/// </summary>
	public class PListBinaryWriter
	{
		/// <summary>
		/// The Header (bplist00)
		/// </summary>
		private static readonly byte[] _header = {
			0x62, 0x70, 0x6c, 0x69, 0x73, 0x74, 0x30, 0x30
		};

		private readonly Dictionary<byte, Dictionary<PNode, int>> _uniqueElements = new Dictionary<byte, Dictionary<PNode, int>>();

		/// <summary>
		/// Initializes a new instance of the <see cref="PListBinaryWriter"/> class.
		/// </summary>
		internal PListBinaryWriter()
		{
		}

		/// <summary>
		/// Writers a <see cref="T:PListNet.PNode"/> to the specified stream.
		/// </summary>
		/// <param name="stream">The stream.</param>
		/// <param name="node">The PList node.</param>
		public void Write(Stream stream, PNode node)
		{
			stream.Write(_header, 0, _header.Length);

			var offsets = new List<int>();
			int nodeCount = GetNodeCount(node);

			byte nodeIndexSize;
			if (nodeCount <= byte.MaxValue) nodeIndexSize = sizeof(byte);
			else if (nodeCount <= Int16.MaxValue) nodeIndexSize = sizeof(Int16);
			else nodeIndexSize = sizeof(Int32);

			int topOffestIdx = WriteInternal(stream, nodeIndexSize, offsets, node);
			nodeCount = offsets.Count;
            
			int offsetTableOffset = (int) stream.Position;


			byte offsetSize;
			if (offsetTableOffset <= Byte.MaxValue) offsetSize = sizeof(Byte);
			else if (offsetTableOffset <= Int16.MaxValue) offsetSize = sizeof(Int16);
			else offsetSize = sizeof(Int32);

			for (int i = 0; i < offsets.Count; i++)
			{
				byte[] buf = null;
				switch (offsetSize)
				{
					case 1:
						buf = new [] { (byte) offsets[i] };
						break;
					case 2:
						buf = BitConverter.GetBytes(IPAddress.HostToNetworkOrder((Int16) offsets[i]));
						break;
					case 4:
						buf = BitConverter.GetBytes(IPAddress.HostToNetworkOrder(offsets[i]));
						break;
				}
				stream.Write(buf, 0, buf.Length);
			}

			var header = new byte[32];
			header[6] = offsetSize;
			header[7] = nodeIndexSize;

			BitConverter.GetBytes(IPAddress.HostToNetworkOrder(nodeCount)).CopyTo(header, 12);
			BitConverter.GetBytes(IPAddress.HostToNetworkOrder(topOffestIdx)).CopyTo(header, 20);
			BitConverter.GetBytes(IPAddress.HostToNetworkOrder(offsetTableOffset)).CopyTo(header, 28);

			stream.Write(header, 0, header.Length);
		}

		/// <summary>
		/// Writers a <see cref="T:PListNet.PNode"/> to the current stream position
		/// </summary>
		/// <param name="stream">The stream.</param>
		/// <param name="nodeIndexSize">The node index size.</param>
		/// <param name="offsets">Node offsets.</param>
		/// <param name="node">The PList node.</param>
		/// <returns>The Idx of the written node</returns>
		internal int WriteInternal(Stream stream, byte nodeIndexSize, List<int> offsets, PNode node)
		{
			int elementIdx = offsets.Count;
			if (node.IsBinaryUnique && node is IEquatable<PNode>)
			{
				if (!_uniqueElements.ContainsKey(node.BinaryTag)) _uniqueElements.Add(node.BinaryTag, new Dictionary<PNode, int>());
				if (!_uniqueElements[node.BinaryTag].ContainsKey(node)) _uniqueElements[node.BinaryTag][node] = elementIdx;
				else
				{
					if (node is PListBool) elementIdx = _uniqueElements[node.BinaryTag][node];
					else return _uniqueElements[node.BinaryTag][node];
				}
			}

			int offset = (int) stream.Position;
			offsets.Add(offset);
			int len = node.BinaryLength;
			var typeCode = (byte) (node.BinaryTag << 4 | (len < 0x0F ? len : 0x0F));
			stream.WriteByte(typeCode);
			if (len >= 0x0F)
			{
				var extLen = NodeFactory.CreateLengthElement(len);
				var binaryTag = (byte) (extLen.BinaryTag << 4 | extLen.BinaryLength);
				stream.WriteByte(binaryTag);
				extLen.WriteBinary(stream);
			}

			var arrayNode = node as PListArray;
			if (arrayNode != null)
			{
				WriteInternal(stream, nodeIndexSize, offsets, arrayNode);
				return elementIdx;
			}

			var dictionaryNode = node as PListDict;
			if (dictionaryNode != null)
			{
				WriteInternal(stream, nodeIndexSize, offsets, dictionaryNode);
				return elementIdx;
			}
				
			node.WriteBinary(stream);
			return elementIdx;
		}

		private void WriteInternal(Stream stream, byte nodeIndexSize, List<int> offsets, PListArray array)
		{
			var nodes = new byte[nodeIndexSize * array.Count];
			var streamPos = stream.Position;

			stream.Write(nodes, 0, nodes.Length);
			for (int i = 0; i < array.Count; i++)
			{
				int elementIdx = WriteInternal(stream, nodeIndexSize, offsets, array[i]);
				FormatIdx(elementIdx, nodeIndexSize).CopyTo(nodes, nodeIndexSize * i);
			}

			stream.Seek(streamPos, SeekOrigin.Begin);
			stream.Write(nodes, 0, nodes.Length);
			stream.Seek(0, SeekOrigin.End);
		}

		private void WriteInternal(Stream stream, byte nodeIndexSize, List<int> offsets, PListDict dictionary)
		{
			var keys = new Byte[nodeIndexSize * dictionary.Count];
			var values = new Byte[nodeIndexSize * dictionary.Count];
			long streamPos = stream.Position;
			stream.Write(keys, 0, keys.Length);
			stream.Write(values, 0, values.Length);

			KeyValuePair<String, PNode>[] elems = dictionary.ToArray();

			for (int i = 0; i < dictionary.Count; i++)
			{
				int elementIdx = WriteInternal(stream, nodeIndexSize, offsets, NodeFactory.CreateKeyElement(elems[i].Key));
				FormatIdx(elementIdx, nodeIndexSize).CopyTo(keys, nodeIndexSize * i);
			}
			for (int i = 0; i < dictionary.Count; i++)
			{
				int elementIdx = WriteInternal(stream, nodeIndexSize, offsets, elems[i].Value);
				FormatIdx(elementIdx, nodeIndexSize).CopyTo(values, nodeIndexSize * i);
			}

			stream.Seek(streamPos, SeekOrigin.Begin);
			stream.Write(keys, 0, keys.Length);
			stream.Write(values, 0, values.Length);
			stream.Seek(0, SeekOrigin.End);
		}

		private static int GetNodeCount(PNode node)
		{
			if (node == null) throw new ArgumentNullException("node");

			// special case: array
			var array = node as PListArray;
			if (array != null)
			{
				var count = 1;
				foreach (var subNode in array)
				{
					count += GetNodeCount(subNode);
				}
				return count;
			}

			// special case: dictionary
			var dictionary = node as PListDict;
			if (dictionary != null)
			{
				var count = 1;
				foreach (var subNode in dictionary.Values)
				{
					count += GetNodeCount(subNode);
				}
				count += dictionary.Keys.Count;
				return count;
			}

			// normal case
			return 1;
		}

		/// <summary>
		/// Formats a node index based on the size.
		/// </summary>
		/// <param name="index">The index.</param>
		/// <param name="nodeIndexSize">The node index size.</param>
		/// <returns>The formated idx.</returns>
		private static byte[] FormatIdx(int index, byte nodeIndexSize)
		{
			switch (nodeIndexSize)
			{
				case 1:
					return new [] { (Byte) index };
				case 2:
					return BitConverter.GetBytes(IPAddress.HostToNetworkOrder((Int16) index));
				case 4:
					return BitConverter.GetBytes(IPAddress.HostToNetworkOrder(index));
				default:
					throw new PListFormatException("Invalid node index size");
			}
		}
	}
}
