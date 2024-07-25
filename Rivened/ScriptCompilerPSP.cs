using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Threading;

namespace Rivened {
	internal class ScriptCompilerPSP: ICompiler {

		
		public (bool, string) ApplyPatches(string filename, string src, string[] patches) {
			throw new NotImplementedException();
		}

		public bool Compile(string filename, string source, out byte[] arr, out string err) {
			arr = null;
			using var stream = new MemoryStream();
			using var wr = new BinaryWriter(stream);
			var strings = new List<(int, string)>();
			var lines = source.Split('\n');
			var labels = new Dictionary<string, ushort>();
			var pendingLabelRefs = new List<(int, string)>();
			byte[] trailer = null;
			for(var lineIdx = 0; lineIdx < lines.Length; lineIdx++) {
				var line = lines[lineIdx];
				var i = 0;
				var dotIdx = 0;
				for(; i < line.Length; i++) {
					if(!char.IsWhiteSpace(line[i])) {
						if(line[i] == '&') {
							var endOfName = line.IndexOf(':', i + 1);
							wr.Flush();
							labels[line[(i + 1)..endOfName]] = (ushort)stream.Position;
							i = endOfName;
						} else if(line[i] == '#') {
							goto skip_line;
						} else {
							dotIdx = line.IndexOf('.', i);
							if(dotIdx == -1) {
								goto skip_line;
							}
							break; // found the opcode, hopefully
						}
					}
				}
				var startPos = stream.Position;
				var opcode = ScriptDecompilerPSP.OpcodeList.Find(op => op.name.Equals(line[i..dotIdx]));
				if (opcode == null) {
					err = lineIdx + 1 + ":" + (i + 1) + ": could not parse '" + opcode + "' into opcode";
					return false;
				}

				// this is a bit hacky, but gets the job done
				if (opcode.name.Equals("trailer")) {
					var trailerSize = Convert.ToInt32(line.Substring(8, 2));

					var byteStrings = line.Substring(11, (trailerSize * 3 - 1)).Split('-');
					trailer = new byte[byteStrings.Length];
					for(int j = 0; j < byteStrings.Length; j++) trailer[j] = Convert.ToByte(byteStrings[j], 16);
					continue; //should be the last line anyway
				}
			
				var lenLen = 0;
				for(; dotIdx + lenLen + 1 < line.Length; lenLen++) {
					if(!char.IsDigit(line[dotIdx + lenLen + 1])) {
						break;
					}
				}
				
				var curWr = wr;
				var commandLength = Convert.ToInt32(line.Substring(dotIdx + 1, lenLen));
				var stringPos = new List<int>();
				var stringPosIdx = 0;
				var done = false;
				for(i = dotIdx + lenLen + 1; i < line.Length; i++) {
					  var currentLineChar = line[i];

					switch(currentLineChar) {
						case '-':
							continue;
						case '~':
							continue;
						case 'S':
							stringPos.Add((int)stream.Position);
							curWr.Write((ushort)0);
							i += 7; //don't write the old string index also into new stream
							continue;
						case '§':
							int end = line.IndexOf('§', i + 1);
							if(end == -1) {
								err = lineIdx + 1 + ":" + (i + 1) + ": §-string must be terminated with another §";
								return false;
							}
							strings.Add((stringPos[stringPosIdx++], line[(i + 1)..end].Trim()));
							i = end;
							continue;
						case '@':
							strings.Add((stringPos[stringPosIdx++], line[(i + 1)..]));
							done = true;
							break;
						case '&': 
							var endLine = i + 1;
							while((line[endLine] >= '0' && line[endLine] <= '9') || 
								(line[endLine] >= 'A' && line[endLine] <= 'Z') || 
								(line[endLine] >= 'a' && line[endLine] <= 'z') || 
								line[endLine] == '_') {
								endLine++;
							}
							var label = line[(i + 1)..endLine];
							if(label.Length == 0) {
								err = lineIdx + 1 + ":" + (i + 1) + ": could not parse label reference";
								return false;
							}
							if(labels.TryGetValue(label, out var location)) {
								curWr.Write((ushort)location);
							} else {
								curWr.Flush(); // this is fine because trailer (which replaces the stream) wouldn't have a &
								pendingLabelRefs.Add(((int)stream.Position, label));
								curWr.Write((ushort)0);
							}
							i = endLine - 1;
							continue;

						default:
							if(i + 1 < line.Length && ((line[i] >= '0' && line[i] <= '9') || 
								(line[i] >= 'A' && line[i] <= 'F') || 
								(line[i] >= 'a' && line[i] <= 'f')) && ((line[i + 1] >= '0' && line[i + 1] <= '9') || 
								(line[i + 1] >= 'A' && line[i + 1] <= 'F') || 
								(line[i + 1] >= 'a' && line[i + 1] <= 'f'))) 
							{
								try {
									curWr.Write(Convert.ToByte(line[i..(i + 2)], 16));
									i++;
								} catch {
									err = lineIdx + 1 + ":" + (i + 1) + ": could not parse data byte";
									return false;
								}

							} else {
								err = lineIdx + 1 + ":" + (i + 1) + ": unexpected character '" + line[i] + '\'';
								return false;
							}
							continue;
					}
					if(done) { break; }
				}
				if(stream.Position != startPos + commandLength) {
					err = lineIdx + 1 + ":1: instruction has length " + (stream.Position - startPos) + " instead of the expected " + lenLen;
					return false;
				}
				skip_line:;
			}
			for(int i = 0; i < pendingLabelRefs.Count; i++) {
				wr.Flush();
				var posBackup = stream.Position;
				stream.Position = pendingLabelRefs[i].Item1;
				wr.Write((ushort)labels[pendingLabelRefs[i].Item2]);
				wr.Flush();
				stream.Position = posBackup;
			}
			
			var encoding = (Encoding)Encoding.GetEncoding("Shift-JIS").Clone();
			encoding.DecoderFallback = DecoderFallback.ExceptionFallback;
			encoding.EncoderFallback = EncoderFallback.ExceptionFallback;
			
			// the start of strings is technically aligned up to 0x10 originally, but that doesn't seem necessary or beneficial
			foreach(var pair in strings) {
				var stringPos = (int)wr.BaseStream.Position;
				if(stringPos > 0xFFFF) {
					err = "string passes 64kb mark: " + pair.Item2;
					return false;
				}
				wr.Flush();
				wr.BaseStream.Position = pair.Item1;
				wr.Write((ushort)stringPos);
				wr.Flush();
				wr.BaseStream.Position = stringPos;
				var str = pair.Item2.Replace('«', '《').Replace('»', '》');

				// no tweaks yet
				//if(MainWindow.Instance.EnTweaks) {
				//	str = EnTweaks.ApplyEnTweaks(str);
				//}
				if(str.Length > 0) {
					if(str[0] == '【') {
						int bracketEnd = str.IndexOf('】') + 1;
						if(bracketEnd != 0) {
							//if(useBig5) {
							//	if(JpToChNames.TryGetValue(str[..bracketEnd], out var chName)) {
							//		str = chName + str[bracketEnd..];
							//	}
							//} else {
							//	if(ChToJpNames.TryGetValue(str[..bracketEnd], out var jpName)) {
							//		str = jpName + str[bracketEnd..];
							//	}
							//}
						}
					}
					
					try {
						wr.Write(encoding.GetBytes(str));
					} catch {
						Console.WriteLine("Error on line: " + str);
						throw;
					}
					
				}
				wr.Write((byte)0);
			}
			
			stream.Seek(0, SeekOrigin.End);
			stream.Write(trailer);
			stream.Flush();
			err = "";

			stream.Seek(0, SeekOrigin.Begin);
			//using(var fs = new FileStream("C:\\Users\\Nils\\Desktop\\CompiledScripts\\" + filename, FileMode.OpenOrCreate)) {
			//	stream.CopyTo(fs);
			//}

			LzssCompress compressor = new LzssCompress();

			byte[] output = new byte[stream.Length];
			stream.Read(output, 0, (int)stream.Length);
			arr = compressor.Compress(output);

			return true;
		}
	}
}
