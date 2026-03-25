using System;
using System.Text;
using System.Linq;
using System.Collections.Generic;
using System.IO;

namespace BitTorrent
{
    /// <summary>
    /// Encodes and decodes values using the bencoding format used by torrent metadata and tracker responses.
    /// </summary>
    public static class BEncoding
    {

        // Encoding system
        private static byte DictionaryStart = System.Text.Encoding.UTF8.GetBytes("d")[0]; // 100
        private static byte DictionaryEnd = System.Text.Encoding.UTF8.GetBytes("e")[0]; // 101
        private static byte ListStart = System.Text.Encoding.UTF8.GetBytes("l")[0]; // 108
        private static byte ListEnd = System.Text.Encoding.UTF8.GetBytes("e")[0]; // 101
        private static byte NumberStart = System.Text.Encoding.UTF8.GetBytes("i")[0]; // 105
        private static byte NumberEnd = System.Text.Encoding.UTF8.GetBytes("e")[0]; // 101
        private static byte ByteArrayDivider = System.Text.Encoding.UTF8.GetBytes(":")[0]; // 58

        #region Decoding
        /// <summary>
        /// Decodes a complete bencoded payload into dictionaries, lists, integers, strings, or byte arrays.
        /// </summary>
        /// <param name="bytes">The raw bencoded bytes to decode.</param>
        /// <returns>The decoded object graph.</returns>
        public static object Decode(byte[] bytes)
        {
            IEnumerator<byte> enumerator = ((IEnumerable<byte>)bytes).GetEnumerator();
            enumerator.MoveNext();
            return DecodeNextObject(enumerator);
        }

        /// <summary>
        /// Reads a file from disk and decodes its contents as bencoded data.
        /// </summary>
        /// <param name="path">The path to the bencoded file.</param>
        /// <returns>The decoded object graph.</returns>
        public static object DecodeFile(string path)
        {
            if (!File.Exists(path))
            {
                throw new FileNotFoundException("unable to find file : " + path);
            }
            byte[] bytes = File.ReadAllBytes(path);
            return Decode(bytes);
        }

        /// <summary>
        /// Decodes the next value at the enumerator's current position.
        /// </summary>
        /// <param name="enumerator">The byte enumerator positioned at the start of the next value.</param>
        /// <returns>The decoded value.</returns>
        public static object DecodeNextObject(IEnumerator<byte> enumerator)
        {
            if (enumerator.Current == DictionaryStart)
            {
                return DecodeDictionary(enumerator);
            }
            if (enumerator.Current == ListStart)
            {
                return DecodeList(enumerator);
            }
            if (enumerator.Current == NumberStart)
            {
                return DecodeNumber(enumerator);
            }

            return DecodeByteArray(enumerator);
        }

        /// <summary>
        /// Decodes a bencoded integer.
        /// </summary>
        /// <param name="enumerator">The byte enumerator positioned on the integer prefix.</param>
        /// <returns>The decoded integer value.</returns>
        public static long DecodeNumber(IEnumerator<byte> enumerator)
        {
            List<byte> bytes = new List<byte>();
            // keep pulling bytes until we hit the end flag
            while (enumerator.MoveNext())
            {
                if (enumerator.Current == NumberEnd)
                {
                    break;
                }
                bytes.Add(enumerator.Current);
            }
            string numAsString = System.Text.Encoding.UTF8.GetString(bytes.ToArray());
            return Int64.Parse(numAsString);
        }

        /// <summary>
        /// Decodes a length-prefixed bencoded byte array.
        /// </summary>
        /// <param name="enumerator">The byte enumerator positioned on the byte array length.</param>
        /// <returns>The decoded byte array.</returns>
        public static byte[] DecodeByteArray(IEnumerator<byte> enumerator)
        {
            List<byte> lengthBytes = new List<byte>();

            // Scan until a divider
            do
            {
                if (enumerator.Current == ByteArrayDivider)
                {
                    break;
                }
                lengthBytes.Add(enumerator.Current);
            }
            while (enumerator.MoveNext());

            string lengthString = System.Text.Encoding.UTF8.GetString(lengthBytes.ToArray());
            int length;

            if (!Int32.TryParse(lengthString, out length))
            {
                throw new Exception("unable to parse the length of the array");
            }

            // read actual byte array
            byte[] bytes = new byte[length];

            for (int i = 0; i < length; i++)
            {
                enumerator.MoveNext();
                bytes[i] = enumerator.Current;
            }
            return bytes;
        }

        /// <summary>
        /// Decodes a bencoded list until its terminating marker is reached.
        /// </summary>
        /// <param name="enumerator">The byte enumerator positioned on the list prefix.</param>
        /// <returns>The decoded list values.</returns>
        private static List<object> DecodeList(IEnumerator<byte> enumerator)
        {
            List<object> list = new List<object>();
            // keep pulling objects until we hit the end flag
            while (enumerator.MoveNext())
            {
                if (enumerator.Current == ListEnd)
                {
                    break;
                }
                list.Add(DecodeNextObject(enumerator));
            }
            return list;
        }

        /// <summary>
        /// Decodes a bencoded dictionary and validates that its keys are byte-sorted.
        /// </summary>
        /// <param name="enumerator">The byte enumerator positioned on the dictionary prefix.</param>
        /// <returns>The decoded dictionary.</returns>
        private static Dictionary<string, object> DecodeDictionary(IEnumerator<byte> enumerator)
        {
            Dictionary<string, object> dict = new Dictionary<string, object>();
            List<string> keys = new List<string>();

            // keep pulling objects until we hit the end flag
            while (enumerator.MoveNext())
            {
                if (enumerator.Current == DictionaryEnd)
                {
                    break;
                }
                // read key
                byte[] keyBytes = DecodeByteArray(enumerator);
                string key = System.Text.Encoding.UTF8.GetString(keyBytes);

                if (!enumerator.MoveNext())
                {
                    throw new Exception("Dictionary ended before value could be read");
                }

                // read value
                object value = DecodeNextObject(enumerator);
                keys.Add(key);
                dict.Add(key, value);
            }

            // verify the incoming keys are sorted correctly
            var sortedKeys = keys.OrderBy(x => BitConverter.ToString(System.Text.Encoding.UTF8.GetBytes(x))); // convert to byte array for comparison
            if (!keys.SequenceEqual(sortedKeys))
            {
                throw new Exception("Dictionary keys are not sorted correctly");
            }
            return dict;
        }
        #endregion

        #region Encoding

        /// <summary>
        /// Encodes a supported .NET value into its bencoded byte representation.
        /// </summary>
        /// <param name="obj">The value to encode.</param>
        /// <returns>The encoded bytes.</returns>
        public static byte[] Encode(object obj)
        {
            MemoryStream buffer = new MemoryStream();
            EncodeNextObject(buffer, obj);
            return buffer.ToArray();
        }

        /// <summary>
        /// Encodes a value and writes the result directly to a file.
        /// </summary>
        /// <param name="obj">The value to encode.</param>
        /// <param name="path">The destination file path.</param>
        public static void EncodeToFile(object obj, string path)
        {
            File.WriteAllBytes(path, Encode(obj));
        }

        /// <summary>
        /// Appends a single bencoded value to an existing memory buffer.
        /// </summary>
        /// <param name="buffer">The output buffer to write into.</param>
        /// <param name="obj">The value to encode.</param>
        public static void EncodeNextObject(MemoryStream buffer, object obj)
        {
            if (obj is byte[])
            {
                EncodeByteArray(buffer, (byte[])obj);
            }
            else if (obj is string)
            {
                EncodeString(buffer, (string)obj);
            }
            else if (obj is long)
            {
                EncodeNumber(buffer, (long)obj);
            }
            else if (obj.GetType() == typeof(List<object>))
            {
                EncodeList(buffer, (List<object>)obj);
            }
            else if (obj.GetType() == typeof(Dictionary<string, object>))
            {
                EncodeDictionary(buffer, (Dictionary<string, object>)obj);
            }
            else
            {
                throw new Exception("Unable to encode : " + obj.GetType());
            }
        }

        /// <summary>
        /// Encodes an integer using the bencoding integer format.
        /// </summary>
        /// <param name="buffer">The output buffer to write into.</param>
        /// <param name="input">The integer value to encode.</param>
        public static void EncodeNumber(MemoryStream buffer, long input)
        {
            buffer.Append(NumberStart);
            buffer.Append(Encoding.UTF8.GetBytes(Convert.ToString(input)));
            buffer.Append(NumberEnd);
        }

        /// <summary>
        /// Encodes a byte array using the bencoding byte string format.
        /// </summary>
        /// <param name="buffer">The output buffer to write into.</param>
        /// <param name="body">The byte array to encode.</param>
        public static void EncodeByteArray(MemoryStream buffer, byte[] body)
        {
            buffer.Append(Encoding.UTF8.GetBytes(Convert.ToString(body.Length)));
            buffer.Append(ByteArrayDivider);
            buffer.Append(body);
        }

        /// <summary>
        /// Encodes a UTF-8 string as a bencoded byte string.
        /// </summary>
        /// <param name="buffer">The output buffer to write into.</param>
        /// <param name="input">The string value to encode.</param>
        public static void EncodeString(MemoryStream buffer, string input)
        {
            EncodeByteArray(buffer, Encoding.UTF8.GetBytes(input));
        }

        /// <summary>
        /// Encodes a list by writing each element in sequence between list markers.
        /// </summary>
        /// <param name="buffer">The output buffer to write into.</param>
        /// <param name="input">The list to encode.</param>
        public static void EncodeList(MemoryStream buffer, List<object> input)
        {
            buffer.Append(ListStart);
            foreach (var item in input)
            {
                EncodeNextObject(buffer, item);
            }
            buffer.Append(ListEnd);
        }

        /// <summary>
        /// Encodes a dictionary after sorting keys by their raw UTF-8 bytes as required by bencoding.
        /// </summary>
        /// <param name="buffer">The output buffer to write into.</param>
        /// <param name="input">The dictionary to encode.</param>
        private static void EncodeDictionary(MemoryStream buffer, Dictionary<string, object> input)
        {
            buffer.Append(DictionaryStart);
            // we need to sort the keys by their raw bytes , not the string
            var sortedKeys = input.Keys.ToList().OrderBy(x => BitConverter.ToString(Encoding.UTF8.GetBytes(x)));
            foreach (var key in sortedKeys)
            {
                EncodeString(buffer, key);
                EncodeNextObject(buffer, input[key]);
            }
            buffer.Append(DictionaryEnd);
        }



        #endregion

    }

    /// <summary>
    /// Adds small helper methods for appending bytes to a <see cref="MemoryStream"/>.
    /// </summary>
    public static class MemorySreamExtensions
    {
        /// <summary>
        /// Writes a single byte to the stream.
        /// </summary>
        /// <param name="stream">The destination stream.</param>
        /// <param name="value">The byte to append.</param>
        public static void Append(this MemoryStream stream, byte value)
        {
            stream.WriteByte(value);
        }

        /// <summary>
        /// Writes a byte array to the stream.
        /// </summary>
        /// <param name="stream">The destination stream.</param>
        /// <param name="values">The bytes to append.</param>
        public static void Append(this MemoryStream stream, byte[] values)
        {
            stream.Write(values, 0, values.Length);
        }
    }
}
