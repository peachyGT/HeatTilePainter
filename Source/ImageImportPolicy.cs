namespace HeatTileOverlayPrototype
{
    internal static class ImageImportPolicy
    {
        // Inspect dimensions before Unity allocates the decoded image. Compressed byte limits
        // alone do not bound the memory required by a very large, mostly uniform image.
        internal static bool HasBoundedDimensions(byte[] bytes, int maximum)
        {
            if(bytes==null || bytes.Length<24) return false;
            int width=0,height=0;
            if(bytes[0]==137 && bytes[1]==80 && bytes[2]==78 && bytes[3]==71
                && bytes[4]==13 && bytes[5]==10 && bytes[6]==26 && bytes[7]==10
                && bytes[12]==73 && bytes[13]==72 && bytes[14]==68 && bytes[15]==82)
            {
                width=Read32(bytes,16);height=Read32(bytes,20);
            }
            else if(bytes[0]==255 && bytes[1]==216)
            {
                int p=2;
                while(p+3<bytes.Length)
                {
                    if(bytes[p++]!=255)return false;
                    while(p<bytes.Length && bytes[p]==255)p++;
                    if(p>=bytes.Length)return false;
                    int marker=bytes[p++];
                    if(marker==217 || marker==218)return false;
                    if(marker==1 || (marker>=208 && marker<=215))continue;
                    if(p+1>=bytes.Length)return false;
                    int length=(bytes[p]<<8)|bytes[p+1];
                    if(length<2 || length>bytes.Length-p)return false;
                    if(marker>=192 && marker<=207 && marker!=196 && marker!=200 && marker!=204)
                    {
                        if(length<8)return false;
                        height=(bytes[p+3]<<8)|bytes[p+4];width=(bytes[p+5]<<8)|bytes[p+6];break;
                    }
                    p+=length;
                }
            }
            return width>0 && height>0 && width<=maximum && height<=maximum;
        }
        static int Read32(byte[] b,int p) { return (b[p]<<24)|(b[p+1]<<16)|(b[p+2]<<8)|b[p+3]; }
    }
}
