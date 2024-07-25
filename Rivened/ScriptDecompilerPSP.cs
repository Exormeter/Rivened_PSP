using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace Rivened {
	
	public class ScriptDecompilerPSP : IDecompiler {
		private volatile bool UseBig5 = true;
		private volatile Dictionary<string, string> Dumps = new Dictionary<string, string>();
		private HashSet<string> Modified = new HashSet<string>();
		private LzssCompress compressor = new LzssCompress();
		public int DumpCount => Dumps.Count;

		public override bool CheckAndClearModified(string filename) {
			if(Modified.Contains(filename)) {
				Modified.Remove(filename);
				return true;
			}
			// the user needs to be able to just reencode, so it turns out we do always need to decompile
			return true;
		}

		public override void ChangeDump(string filename, string data) {
			Trace.Assert(Dumps.ContainsKey(filename));
			Dumps[filename] = data;
			Modified.Add(filename);
		}

		public override string DumpStrings(byte[] bytes, ref (int, int) strsBounds, int dataPos, bool useBig5 = false, params int[] strsPos) {
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
					res += "~@" + str;
				} else {
					res += "~§" + str + '§';
				}
			}
			return res;
		}

		public override string Decompile(AFS afs, AFS.Entry entry) {
			if(Dumps.TryGetValue(entry.Name, out var dump)) {
				return dump;
			}
			byte[] containerBytes = entry.Load(afs);

			//LZSS0 compressed data starts at offset
			byte[] compressedScript = new byte[containerBytes.Length - 4];
			Array.Copy(containerBytes, 4, compressedScript, 0, compressedScript.Length);
			return Decompile(entry.Name, compressor.Decompress(compressedScript));
		}

		public override string Decompile(string filename, byte[] bytes) {
			if(Dumps.TryGetValue(filename, out var cached)) {
				return cached;
			}

			//using(var fs = new FileStream("C:\\Users\\Nils\\Desktop\\CleanDump\\" + filename, FileMode.OpenOrCreate)) {
			//	fs.Write(bytes, 0, bytes.Length);
			//	fs.Flush();
			//}

			var pos = 0;
			var strsBounds = (0xFFFFFF, 0);
			var offsetsToLines = new Dictionary<uint, int>(); // (binary location, decompiled string position) the first is for the header line
			var labels = new SortedSet<uint>(); // destination position
			var lastOffset = 0;
			var res = "";
				
			while(true) {
				var op = OpcodeList[bytes[pos]];
				var fullLen = op.length;
				switch(op.name) {

					case "sel_disp2":
						fullLen += bytes[pos + 1] * 0x08;
						break;
						 
					case "graph_disp":
						fullLen += bytes[pos + 1] * 0x10;
						break;
				}
						
				if(fullLen == 0) {
					break;
				}
				var len = fullLen; // now pos and len are for the data, excluding the opcode
				var instDump = op.name + '.' + fullLen;

				switch(op.name) {

					case "msg_disp2":
						lastOffset = (ushort)((bytes[pos + 5] << 8) | bytes[pos + 4]);
						instDump += '~' + BitConverter.ToString(bytes, pos, 4) + "-S-" + BitConverter.ToString(bytes, pos + 4 , len - 4) + DumpStrings(bytes, ref strsBounds, pos, false, 4);
						break;

					case "sel_disp2":
						instDump += '~' + BitConverter.ToString(bytes, pos, 6);
						var count = bytes[pos + 1];
						var choices = new int[count];
						for(int i = 0; i < count; i++) {
							var choicePos = i * 8 + 6;
							lastOffset = (ushort)((bytes[pos + choicePos + 1] << 8) | bytes[pos + choicePos]);
							instDump += "-S-" + BitConverter.ToString(bytes, pos + choicePos, 8);
							choices[i] = choicePos;
						}
						instDump += DumpStrings(bytes, ref strsBounds, pos, false, choices);
						break;

					default:
							instDump += '~' + BitConverter.ToString(bytes, pos, len);
							break;
				}


				res += instDump + '\n';
				pos += len;
				
				if (pos >= strsBounds.Item1) {
					while(bytes[lastOffset] != 0) { lastOffset++; }
					var trailerLength = bytes.Length - lastOffset;
					trailerLength--;
					res += "trailer" + '.' + trailerLength + '~' + BitConverter.ToString(bytes, lastOffset + 1, trailerLength);
					break;
				}
			}
				 
			Dumps[filename] = res;
			return res;
		}

		public class Opcode {
			public string name;
			public byte opcodeByte;
			public int length;

			public Opcode(string name, byte opcodeByte, int length) {
				this.name = name;
				this.opcodeByte = opcodeByte;
				this.length = length;
			}
		}

		static public List<Opcode> OpcodeList = new List<Opcode>
		{
			new Opcode("nop", 0x00, 0x1),
			new Opcode("end", 0x01, 2),
			new Opcode("if", 0x02, 10),
			new Opcode("int_goto", 0x03, 4), // probably 4
			new Opcode("int_call", 0x04, 4), // probably 4
			new Opcode("int_return", 0x05, 0xFF),
			new Opcode("ext_goto", 0x06, 2),
			new Opcode("ext_call", 0x07, 12),
			new Opcode("ext_return", 0x08, 2),
			new Opcode("reg_calc", 0x09, 6),
			new Opcode("count_clear", 0x0A, 2),
			new Opcode("count_wait", 0x0B, 4),
			new Opcode("time_wait", 0x0C, 4),
			new Opcode("pad_wait", 0x0D, 4),
			new Opcode("pad_get", 0x0E, 4),
			new Opcode("file_read", 0x0F, 4),
			new Opcode("file_wait", 0x10, 2),
			new Opcode("msg_wind", 0x11, 2),
			new Opcode("msg_view", 0x12, 2),
			new Opcode("msg_mode", 0x13, 2),
			new Opcode("msg_pos,", 0x14, 6),
			new Opcode("msg_size", 0x15, 6),
			new Opcode("msg_type", 0x16, 2),
			new Opcode("msg_coursor", 0x17, 6),
			new Opcode("msg_set", 0x18, 4),
			new Opcode("msg_wait", 0x19, 2),
			new Opcode("msg_clear", 0x1A, 2),
			new Opcode("msg_line", 0x1B, 2),
			new Opcode("msg_speed", 0x1C, 2),
			new Opcode("msg_color", 0x1D, 2),
			new Opcode("msg_anim", 0x1E, 2),
			new Opcode("msg_disp", 0x1F, 10),
			new Opcode("sel_set", 0x20, 4),
			new Opcode("sel_entry", 0x21, 6),
			new Opcode("sel_view", 0x22, 2),
			new Opcode("sel_wait", 0x23, 0xFF),
			new Opcode("sel_style", 0x24, 2),
			new Opcode("sek_disp", 0x25, 4),
			new Opcode("fade_start", 0x26, 4),
			new Opcode("fade_wait", 0x27, 2),
			new Opcode("graph_set", 0x28, 4),
			new Opcode("graph_del", 0x29, 2),
			new Opcode("graph_cpy", 0x2A, 4),
			new Opcode("grsaph_view", 0x2B, 6),
			new Opcode("graph_pos", 0x2C, 6),
			new Opcode("graph_move", 0x2D, 0xFF),
			new Opcode("graph_prio", 0x2E, 4),
			new Opcode("graph_anim", 0x2F, 4),
			new Opcode("graph_pal", 0x30, 4),
			new Opcode("graph_lay", 0x31, 4),
			new Opcode("graph_wait", 0x32, 4),
			new Opcode("graph_disp", 0x33, 4), // 4 is minimum, Token length is variable
			new Opcode("effect_start", 0x34, 4),
			new Opcode("effect_end", 0x35, 2),
			new Opcode("effect_wait", 0x36, 2),
			new Opcode("bgm_set", 0x37, 2),
			new Opcode("bgm_del", 0x38, 2),
			new Opcode("bgm_req", 0x39, 2),
			new Opcode("bgm_wait", 0x3A, 2),
			new Opcode("bgm_speed", 0x3B, 4),
			new Opcode("bgm_vol", 0x3C, 2),
			new Opcode("se_set", 0x3D, 2),
			new Opcode("se_del", 0x3E, 2),
			new Opcode("se_req", 0x3F, 4),
			new Opcode("se_wait", 0x40, 4),
			new Opcode("se_speed", 0x41, 4),
			new Opcode("se_vol", 0x42, 4),
			new Opcode("voice_set", 0x43, 2),
			new Opcode("voice_del", 0x44, 2),
			new Opcode("voice_req", 0x45, 2),
			new Opcode("voice_wait", 0x46, 2),
			new Opcode("voice_speed", 0x47, 4),
			new Opcode("voice_vol", 0x48, 2),
			new Opcode("menu_lock", 0x49, 2),
			new Opcode("save_lock", 0x4A, 2),
			new Opcode("save_check", 0x4B, 4),
			new Opcode("save_disp", 0x4C, 4),
			new Opcode("disk_change", 0x4D, 4),
			new Opcode("jamp_start", 0x4E, 4),
			new Opcode("jamp_end", 0x4F, 2),
			new Opcode("task_entry", 0x50, 4),
			new Opcode("task_del", 0x51, 2),
			new Opcode("cal_disp", 0x52, 4),
			new Opcode("title_disp", 0x53, 2),
			new Opcode("vib_start", 0x54, 4),
			new Opcode("vib_end", 0x55, 2),
			new Opcode("vib_wait", 0x56, 2),
			new Opcode("map_view", 0x57, 4),
			new Opcode("map_entry", 0x58, 4),
			new Opcode("map_disp", 0x59, 4),
			new Opcode("edit_view", 0x5A, 4),
			new Opcode("chat_send", 0x5B, 4),
			new Opcode("chat_msg", 0x5C, 4),
			new Opcode("chat_entry", 0x5D, 4),
			new Opcode("chat_exit", 0x5E, 4),
			new Opcode("null", 0x5F, 1),
			new Opcode("movie_play", 0x60, 4),
			new Opcode("graph_pos_auto", 0x61, 12),
			new Opcode("graph_pos_save", 0x62, 2),
			new Opcode("graph_uv_auto", 0x63, 16),
			new Opcode("graph_uv_save", 0x64, 2),
			new Opcode("effect_ex", 0x65, 38),
			new Opcode("fade_ex", 0x66, 0xFF),
			new Opcode("vib_ex", 0x67, 6),
			new Opcode("clock_disp", 0x68, 6),
			new Opcode("graph_disp_ex", 0x69, 0x18),
			new Opcode("map_init_ex", 0x6A, 4),
			new Opcode("map_point_ex", 0x6B, 4),
			new Opcode("map_route_ex", 0x6C, 4),
			new Opcode("quick_save", 0x6D, 2),
			new Opcode("trace_pc", 0x6E, 2),
			new Opcode("sys_msg", 0x6F, 4),
			new Opcode("skip_lock", 0x70, 2),
			new Opcode("key_lock", 0x71, 2),
			new Opcode("graph_disp2", 0x72, 0xFF),
			new Opcode("msg_disp2", 0x73, 12),
			new Opcode("sel_disp2", 0x74, 6),
			new Opcode("date_disp", 0x75, 8),
			new Opcode("vr_disp", 0x76, 4),
			new Opcode("vr_select", 0x77, 4),
			new Opcode("vr_reg_calc", 0x78, 4),
			new Opcode("vr_msg_disp", 0x79, 4),
			new Opcode("map_select", 0x7A, 4),
			new Opcode("ecg_set", 0x7B, 4),
			new Opcode("ev_init", 0x7C, 4),
			new Opcode("ev_disp", 0x7D, 4),
			new Opcode("ev_anim", 0x7E, 4),
			new Opcode("eye_lock", 0x7F, 2),
			new Opcode("msg_log", 0x80, 4),
			new Opcode("graph_scale_auto", 0x81, 16),
			new Opcode("movie_start", 0x82, 2),
			new Opcode("move_end", 0x83, 2),
			new Opcode("fade_ex_strt", 0x84, 6),
			new Opcode("fade_ex_wait", 0x85, 2),
			new Opcode("breath_lock", 0x86, 2),
			new Opcode("g3d_disp", 0x87, 2),
			new Opcode("staff_start", 0x88, 6),
			new Opcode("staff_end", 0x89, 2),
			new Opcode("staff_wait", 0x8A, 2),
			new Opcode("scroll_lock", 0x8B, 2),
			new Opcode("trailer", 0xFF, 0)
		};

	}
}
