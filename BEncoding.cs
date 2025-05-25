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





    }
}