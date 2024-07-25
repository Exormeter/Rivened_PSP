using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Rivened {
	public interface ICompiler {
		public abstract (bool, string) ApplyPatches(string filename, string src, string[] patches);

		public abstract bool Compile(string filename, string source, out byte[] arr, out string err);
	}
}
