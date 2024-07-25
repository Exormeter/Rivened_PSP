using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Rivened {
	public abstract class IDecompiler {
		public abstract bool CheckAndClearModified(string filename);

		public abstract void ChangeDump(string filename, string data);

		public abstract string Decompile(AFS afs, AFS.Entry entry);

		public abstract string Decompile(string filename, byte[] bytes);

		/// <summary>
		/// Dumps strings from the text section
		/// </summary>
		/// <param name="bytes">Scene file bytes</param>
		/// <param name="strsBounds">String start/end positions</param>
		/// <param name="dataPos">"Offset to command bytes"</param>
		/// <param name="strsPos">"Offsets to string inside command bytes"</param>
		/// <returns></returns>
		public virtual string DumpStrings(byte[] bytes, ref (int, int) strsBounds, int dataPos, bool useBig5 = false, params int[] strsPos) {
			string res = "";
			for(int i = 0; i < strsPos.Length; i++) {
				var strStart = bytes[dataPos + strsPos[i]] | bytes[dataPos + strsPos[i] + 1] << 8;
				int strEnd = strStart;
				while(bytes[strEnd] != 0) {
					strEnd++;
				}
				if(strStart < strsBounds.Item1) {
					strsBounds.Item1 = strStart;
				}
				if(strEnd + 1 > strsBounds.Item2) {
					strsBounds.Item2 = strEnd + 1;
				}
				var str = useBig5 ? Big5.Decode(bytes.AsSpan(strStart..strEnd)) :
				 	Program.SJIS.GetString(bytes.AsSpan(strStart..strEnd));
				if(i + 1 == strsPos.Length) {
					res += " @" + str;
				} else {
					res += " §" + str + '§';
				}
			}
			return res;
		}
	}
}
