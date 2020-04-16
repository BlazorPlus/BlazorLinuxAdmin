using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

static class BLA_StaticExts
{
    //fix a bug for raspbian
    static public int SafeIndexOf(this string self, string part, int pos)
    {
        char fc = part[0];
        while (pos < self.Length)
        {
            pos = self.IndexOf(fc, pos);
            if (pos == -1)
                return -1;
            if (string.Compare(self, pos, part, 0, part.Length, false) == 0)
                return pos;
            pos++;
        }
        return -1;
    }
}
