using System;
using System.Text;
using System.Linq;
using System.Collections.Generic;
using System.IO;

namespace BitTorrent{
    public static class BEncoding{
        
        // Encoding system
        private static byte DictionaryStart = System.Text.Encoding.UTF8.GetBytes("d")[0]; // 100
        private static byte DictionaryEnd = System.Text.Encoding.UTF8.GetBytes("e")[0]; // 101
        private static byte ListStart = System.Text.Encoding.UTF8.GetBytes("l")[0]; // 108
        private static byte ListEnd = System.Text.Encoding.UTF8.GetBytes("e")[0]; // 101
        private static byte NumberStart = System.Text.Encoding.UTF8.GetBytes("i")[0]; // 105
        private static byte NumberEnd = System.Text.Encoding.UTF8.GetBytes("e")[0]; // 101
        private static byte ByteArrayDivider = System.Text.Encoding.UTF8.GetBytes(":")[0]; // 58

        # region Decoding
        public static object Decode(byte[] bytes){
            IEnumerator<byte> enumerator = ((IEnumerable<byte>) bytes).GetEnumerator();
            enumerator.MoveNext();
            return DecodeNextObject(enumerator);
        }

        public static object DecodeFile(string path){
            if(!File.Exists(path)){
                throw new FileNotFoundException("unable to find file : "+ path);
            }
            byte[] bytes = File.ReadAllBytes(path);
            return BEncoding.Decode(bytes);
        }

        public  static object DecodeNextObject( IEnumerator<byte> enumerator){
            if (enumerator.Current == DictionaryStart){
                return DecodeDictionary(enumerator);
            }
            if (enumerator.Currrent == ListStart){
                return DecodeList(enumerator);
            }
            if (enumerator.Current == NumberStart){
                return DecodeNumber(enumerator);
            }

            return DecodeByteArray(enumerator);
        }

        // Decodes the number and return as long 
        public static long DecodeNumber(IEnumerator<byte> enumerator){
            List<byte> bytes = new List<byte>();
            // keep pulling bytes until we hit the end flag
            while (enumerator.MoveNext()){
                if(enumerator.Current == NumberEnd){
                    break;
                }
                bytes.Add(enumerator.Current);
            }
            string numAsString = System.Text.Encoding.UTF8.GetString(bytes.ToArray());
            return Int64.Parse(numAsString);
        }

        // Decode byte array
        public static byte[] DecodeByteArray(IEnumerator<byte> enumerator){
            List<byte> lengthBytes = new List<byte>();

            // Scan until a divider
            do{
                if(enumerator.Current == ByteArrayDivider){
                    break;
                }
                lengthBytes.Add(enumerator.Current);
            }
            while ( enumerator.MoveNext());

            string lengthString  = System.Text.Encoding.UTF8.GetString(lengthBytes.ToArray());
            int length;

            if (!Int32.TryParse(lengthString, out length)){
                throw new Exception("unable to parse the length of the array");
            }

            // read actual byte array
            byte[] bytes = new byte[length];

            for (int i = 0 ; i < length ; i ++){
                enumerator.MoveNext();
                bytes[i] = enumerator.Current;
            }
            return bytes;
        }

        private static DecodeList(IEnumerator<byte> enumerator){
            List<object> list = new List<object>();
            // keep pulling objects until we hit the end flag
            while (enumerator.MoveNext()){
                if(enumerator.Current == ListEnd){
                    break;
                }
                list.Add(DecodeNextObject(enumerator));
            }
            return list;
        }

        // Decode dictionary
        private static DecodeDictionary(IEnumerator<byte> enumerator){
            Dictionary<string, object> dict = new Dictionary<string, object>();
            List<string> keys = new List<string>();

            // keep pulling objects until we hit the end flag
            while (enumerator.MoveNext()){
                if(enumerator.Current == DictionaryEnd){
                    break;
                }
                // read key
                byte[] keyBytes = DecodeByteArray(enumerator);
                string key = System.Text.Encoding.UTF8.GetString(keyBytes);
                // read value
                object value = DecodeNextObject(enumerator);
                keys.Add(key);
                dict.Add(key, value);
            }

            // verify the incoming keys are sorted correctly
            var sortedKeys = keys.OrderBy(x => BitConverter.ToString(System.Text.Encoding.UTF8.GetBytes(x))); // convert to byte array for comparison
            if (!keys.SequenceEqual(sortedKeys)){
                throw new Exception("Dictionary keys are not sorted correctly");
            }
            return dict;
        }
        # endregion

        # region Encoding






    }
}