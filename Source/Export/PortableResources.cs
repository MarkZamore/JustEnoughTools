// Adapted from the user-owned Autonomy&Performance project; resource and Shell fixes retained.
namespace JustEnoughTools.Export {
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

// A deliberately small PE resource writer for our unsigned Framework compiler output.
// It runs entirely in memory and never calls the Windows resource-update API.
internal static class PortableResources
{
    const int MaximumImageSize=64*1024*1024;
    sealed class Key : IComparable<Key>
    {
        public int Id;
        public string Name;
        public Key(int id){Id=id;}
        public Key(string name){Name=name;}
        public int CompareTo(Key other){if(Name!=null)return other.Name==null?-1:StringComparer.Ordinal.Compare(Name,other.Name);return other.Name!=null?1:Id.CompareTo(other.Id);}
    }
    sealed class Node
    {
        public SortedDictionary<Key,Node> Children;
        public byte[] Header;
        public byte[] Data;
        public uint CodePage,Reserved;
        public int Offset,DataOffset;
        public static Node Directory(){return new Node{Children=new SortedDictionary<Key,Node>(),Header=new byte[16]};}
    }
    sealed class Section
    {
        public string Name;
        public int Header,RawOffset,RawSize,VirtualAddress,VirtualSize;
    }
    sealed class Image
    {
        public byte[] Bytes;
        public int Optional,Directories,FileAlignment,SectionAlignment,HeaderSize,ResourceSize;
        public Section Text,Resources,Relocations;
        public Node Root;
    }
    static InvalidDataException Invalid(){return new InvalidDataException("Неподдерживаемая или повреждённая структура исполняемого файла.");}
    static void Require(bool condition){if(!condition)throw Invalid();}
    static void Range(byte[] bytes,int offset,int length){Require(offset>=0&&length>=0&&(long)offset+length<=bytes.Length);}
    static ushort U16(byte[] bytes,int offset){Range(bytes,offset,2);return BitConverter.ToUInt16(bytes,offset);}
    static uint U32(byte[] bytes,int offset){Range(bytes,offset,4);return BitConverter.ToUInt32(bytes,offset);}
    static int I32(byte[] bytes,int offset){uint value=U32(bytes,offset);Require(value<=Int32.MaxValue);return (int)value;}
    static void Put(byte[] bytes,int offset,int value){Range(bytes,offset,4);Buffer.BlockCopy(BitConverter.GetBytes(value),0,bytes,offset,4);}
    static int Align(int value,int alignment){Require(value>=0&&alignment>0&&(alignment&(alignment-1))==0);long aligned=((long)value+alignment-1)&~((long)alignment-1);Require(aligned<=MaximumImageSize);return (int)aligned;}
    static byte[] Slice(byte[] bytes,int offset,int length){Range(bytes,offset,length);byte[] result=new byte[length];Buffer.BlockCopy(bytes,offset,result,0,length);return result;}
    static bool Within(int value,int size,Section section){return value>=section.VirtualAddress&&(long)value+size<=(long)section.VirtualAddress+section.VirtualSize;}
    static Image Parse(byte[] bytes)
    {
        Require(bytes!=null&&bytes.Length>=512&&bytes.Length<=MaximumImageSize);
        Require(U16(bytes,0)==0x5a4d);int pe=I32(bytes,60);Require(U32(bytes,pe)==0x00004550&&U16(bytes,pe+4)==0x8664);
        int count=U16(bytes,pe+6),optional=pe+24,optionalSize=U16(bytes,pe+20);Require((count==2||count==3)&&optionalSize>=240&&U16(bytes,optional)==0x20b);
        Require(U32(bytes,pe+12)==0&&U32(bytes,pe+16)==0); // No COFF symbol table or overlay to relocate.
        Image image=new Image{Bytes=bytes,Optional=optional,Directories=optional+112,FileAlignment=I32(bytes,optional+36),SectionAlignment=I32(bytes,optional+32),HeaderSize=I32(bytes,optional+60)};
        Require(image.FileAlignment>=512&&image.SectionAlignment>=image.FileAlignment&&image.HeaderSize<=bytes.Length&&I32(bytes,optional+108)==16);
        Align(0,image.FileAlignment);Align(0,image.SectionAlignment);
        int table=optional+optionalSize;Range(bytes,table,40*count);Require(table+40*count<=image.HeaderSize);
        Section[] sections=new Section[count];
        for(int i=0;i<count;i++){
            int h=table+i*40;Section section=new Section{Name=Encoding.ASCII.GetString(bytes,h,8).TrimEnd('\0'),Header=h,VirtualSize=I32(bytes,h+8),VirtualAddress=I32(bytes,h+12),RawSize=I32(bytes,h+16),RawOffset=I32(bytes,h+20)};
            Require(section.RawSize>0&&section.VirtualSize>0&&section.RawOffset%image.FileAlignment==0&&section.RawSize%image.FileAlignment==0&&section.VirtualAddress%image.SectionAlignment==0);
            Range(bytes,section.RawOffset,section.RawSize);Require(section.VirtualSize<=section.RawSize);
            Require(U32(bytes,h+24)==0&&U32(bytes,h+28)==0&&U32(bytes,h+32)==0);
            if(i==0)Require(section.RawOffset==image.HeaderSize);
            else Require(section.RawOffset==sections[i-1].RawOffset+sections[i-1].RawSize&&section.VirtualAddress>=Align(sections[i-1].VirtualAddress+sections[i-1].VirtualSize,image.SectionAlignment));
            sections[i]=section;
        }
        Require(sections[0].Name==".text"&&sections[1].Name==".rsrc"&&(count==2||sections[2].Name==".reloc"));
        image.Text=sections[0];image.Resources=sections[1];image.Relocations=count==3?sections[2]:null;
        Section last=sections[count-1];Require(last.RawOffset+last.RawSize==bytes.Length);
        Require(I32(bytes,image.Directories+16)==image.Resources.VirtualAddress);image.ResourceSize=I32(bytes,image.Directories+20);Require(image.ResourceSize>0&&image.ResourceSize<=image.Resources.VirtualSize);
        // All executable/CLR directories stay in .text. Only the resource and relocation directories may move.
        for(int i=0;i<16;i++){
            int rva=I32(bytes,image.Directories+8*i),size=I32(bytes,image.Directories+8*i+4);
            if(i==2)continue;
            if(i==5&&image.Relocations!=null){Require(rva==image.Relocations.VirtualAddress&&size<=image.Relocations.VirtualSize);continue;}
            Require((rva==0&&size==0)||(i!=4&&Within(rva,size,image.Text)));
        }
        int cli=I32(bytes,image.Directories+14*8);Require(Within(cli,72,image.Text));int cliFile=image.Text.RawOffset+cli-image.Text.VirtualAddress;
        Require((U32(bytes,cliFile+16)&8)==0&&U32(bytes,cliFile+32)==0&&U32(bytes,cliFile+36)==0); // Unsigned managed image.
        image.Root=ReadNode(image,0,0,new HashSet<int>());Require(image.Root.Children!=null);return image;
    }
    static void ResourceRange(Image image,int offset,int size){Require(offset>=0&&size>=0&&(long)offset+size<=image.ResourceSize);}
    static Node ReadNode(Image image,int offset,int depth,HashSet<int> visited)
    {
        Require(depth<=8&&visited.Add(offset));ResourceRange(image,offset,16);int at=image.Resources.RawOffset+offset;
        int named=U16(image.Bytes,at+12),ids=U16(image.Bytes,at+14),count=named+ids;ResourceRange(image,offset,16+8*count);
        Node node=Node.Directory();node.Header=Slice(image.Bytes,at,16);
        for(int i=0;i<count;i++){
            uint name=U32(image.Bytes,at+16+8*i),child=U32(image.Bytes,at+20+8*i);Key key;
            if((name&0x80000000)!=0){int start=(int)(name&0x7fffffff);ResourceRange(image,start,2);int length=U16(image.Bytes,image.Resources.RawOffset+start);ResourceRange(image,start+2,length*2);key=new Key(Encoding.Unicode.GetString(image.Bytes,image.Resources.RawOffset+start+2,length*2));Require(i<named);}
            else{Require(i>=named&&name<=Int32.MaxValue);key=new Key((int)name);}
            Node entry;
            if((child&0x80000000)!=0)entry=ReadNode(image,(int)(child&0x7fffffff),depth+1,visited);
            else{
                Require(child<=Int32.MaxValue);int data=(int)child;ResourceRange(image,data,16);int pointer=image.Resources.RawOffset+data;
                int rva=I32(image.Bytes,pointer),size=I32(image.Bytes,pointer+4);Require(Within(rva,size,image.Resources));
                entry=new Node{Data=Slice(image.Bytes,image.Resources.RawOffset+rva-image.Resources.VirtualAddress,size),CodePage=U32(image.Bytes,pointer+8),Reserved=U32(image.Bytes,pointer+12)};
            }
            Require(!node.Children.ContainsKey(key));node.Children.Add(key,entry);
        }
        return node;
    }
    static Node Child(Node parent,Key key,bool create)
    {
        Require(parent.Children!=null);Node result;
        if(!parent.Children.TryGetValue(key,out result)&&create){result=Node.Directory();parent.Children.Add(key,result);}
        return result;
    }
    static void Set(Node root,int type,Key name,ushort language,byte[] bytes)
    {
        Node names=Child(root,new Key(type),true),languages=Child(names,name,true);Require(languages.Children!=null);
        languages.Children[new Key(language)]=new Node{Data=bytes};
    }
    static void Directories(Node node,List<Node> directories,List<Node> leaves)
    {
        if(node.Children==null){leaves.Add(node);return;}
        directories.Add(node);foreach(Node child in node.Children.Values)Directories(child,directories,leaves);
    }
    static byte[] BuildTree(Node root,int resourceRva)
    {
        List<Node> directories=new List<Node>(),leaves=new List<Node>();Directories(root,directories,leaves);
        int offset=0;foreach(Node node in directories){node.Offset=offset;offset=checked(offset+16+node.Children.Count*8);}
        Dictionary<string,int> names=new Dictionary<string,int>(StringComparer.Ordinal);
        foreach(Node node in directories)foreach(Key key in node.Children.Keys)if(key.Name!=null&&!names.ContainsKey(key.Name)){Require(key.Name.Length<=UInt16.MaxValue);names.Add(key.Name,offset);offset=checked(offset+2+key.Name.Length*2);}
        offset=Align(offset,4);foreach(Node leaf in leaves){leaf.Offset=offset;offset=checked(offset+16);}
        foreach(Node leaf in leaves){offset=Align(offset,4);leaf.DataOffset=offset;offset=checked(offset+leaf.Data.Length);Require(offset<=MaximumImageSize);}
        byte[] result=new byte[offset];
        using(BinaryWriter writer=new BinaryWriter(new MemoryStream(result),Encoding.Unicode)){
            foreach(Node node in directories){
                writer.BaseStream.Position=node.Offset;writer.Write(node.Header,0,12);int named=0;foreach(Key key in node.Children.Keys)if(key.Name!=null)named++;
                Require(named<=UInt16.MaxValue&&node.Children.Count-named<=UInt16.MaxValue);writer.Write((ushort)named);writer.Write((ushort)(node.Children.Count-named));
                foreach(KeyValuePair<Key,Node> pair in node.Children){writer.Write(pair.Key.Name!=null?0x80000000u|(uint)names[pair.Key.Name]:(uint)pair.Key.Id);writer.Write(pair.Value.Children!=null?0x80000000u|(uint)pair.Value.Offset:(uint)pair.Value.Offset);}
            }
            foreach(KeyValuePair<string,int> name in names){writer.BaseStream.Position=name.Value;writer.Write((ushort)name.Key.Length);writer.Write(Encoding.Unicode.GetBytes(name.Key));}
            foreach(Node leaf in leaves){writer.BaseStream.Position=leaf.Offset;writer.Write(checked((uint)(resourceRva+leaf.DataOffset)));writer.Write((uint)leaf.Data.Length);writer.Write(leaf.CodePage);writer.Write(leaf.Reserved);writer.BaseStream.Position=leaf.DataOffset;writer.Write(leaf.Data);}
        }
        return result;
    }
    static byte[] Rebuild(Image image)
    {
        byte[] resource=BuildTree(image.Root,image.Resources.VirtualAddress);int rawSize=Align(resource.Length,image.FileAlignment);
        int resourceEnd=checked(image.Resources.RawOffset+rawSize),length=resourceEnd,relocationRva=0;
        if(image.Relocations!=null){length=checked(length+image.Relocations.RawSize);relocationRva=Align(checked(image.Resources.VirtualAddress+resource.Length),image.SectionAlignment);}
        Require(length<=MaximumImageSize);byte[] result=new byte[length];Buffer.BlockCopy(image.Bytes,0,result,0,image.Resources.RawOffset);Buffer.BlockCopy(resource,0,result,image.Resources.RawOffset,resource.Length);
        Put(result,image.Resources.Header+8,resource.Length);Put(result,image.Resources.Header+16,rawSize);Put(result,image.Directories+20,resource.Length);
        int initialized=I32(image.Bytes,image.Optional+8);Put(result,image.Optional+8,checked(initialized+rawSize-image.Resources.RawSize));
        int imageEnd=checked(image.Resources.VirtualAddress+resource.Length);
        if(image.Relocations!=null){
            Buffer.BlockCopy(image.Bytes,image.Relocations.RawOffset,result,resourceEnd,image.Relocations.RawSize);
            Put(result,image.Relocations.Header+12,relocationRva);Put(result,image.Relocations.Header+20,resourceEnd);Put(result,image.Directories+5*8,relocationRva);
            imageEnd=checked(relocationRva+image.Relocations.VirtualSize);
        }
        Put(result,image.Optional+56,Align(imageEnd,image.SectionAlignment));Put(result,image.Optional+64,0);
        // Parse the result before anything is written to disk, including its unchanged CLR directory.
        Parse(result);return result;
    }
    internal static byte[] CreateExecutable(byte[] template,byte[] icon,JustEnoughTools.Actions.FixedAction action = JustEnoughTools.Actions.FixedAction.Security)
    {
        Image image=Parse(template); IconFile.Validate(icon);
        Set(image.Root,10,new Key(JustEnoughTools.Actions.FixedActionResource.ResourceId),0,JustEnoughTools.Actions.FixedActionResource.Encode(action));
        image.Root.Children.Remove(new Key(3));image.Root.Children.Remove(new Key(14));
        Require(icon!=null&&icon.Length>=22&&U16(icon,0)==0&&U16(icon,2)==1);int count=U16(icon,4);Require(count>0&&count<=256);Range(icon,6,count*16);
        using(MemoryStream group=new MemoryStream())using(BinaryWriter writer=new BinaryWriter(group)){
            writer.Write((ushort)0);writer.Write((ushort)1);writer.Write((ushort)count);
            for(int i=0;i<count;i++){int at=6+i*16,size=I32(icon,at+8),offset=I32(icon,at+12);Require(size>=4&&offset>=6+16*count);Set(image.Root,3,new Key(i+1),0,Slice(icon,offset,size));writer.Write(icon,at,8);writer.Write((uint)size);writer.Write((ushort)(i+1));}
            Set(image.Root,14,new Key(1),0,group.ToArray());
            // A content-specific alias lets pinned shortcuts distinguish different icons
            // at the same EXE path. Both groups share the existing RT_ICON images.
            byte[] digest;using(System.Security.Cryptography.SHA256 hash=System.Security.Cryptography.SHA256.Create())digest=hash.ComputeHash(icon);
            int alias=2+(int)(BitConverter.ToUInt32(digest,0)%65534);
            Set(image.Root,14,new Key(alias),0,group.ToArray());
        }
        byte[] output=Rebuild(image);
        AssertIcon(output,icon);
        return output;
    }
    internal static void AssertIcon(byte[] executable,byte[] icon)
    {
        IconFile.Validate(icon);
        Node root=Parse(executable).Root;
        Node groups=Child(root,new Key(14),false),icons=Child(root,new Key(3),false);
        Require(groups!=null&&icons!=null);
        Node group=Child(Child(groups,new Key(1),false),new Key(0),false);
        int count=U16(icon,4);
        Require(group!=null&&group.Children==null&&group.Data.Length==6+14*count&&U16(group.Data,4)==count);
        for(int i=0;i<count;i++){
            int at=6+i*16,entry=6+i*14,size=I32(icon,at+8),offset=I32(icon,at+12);
            for(int j=0;j<8;j++)Require(icon[at+j]==group.Data[entry+j]);
            Require(I32(group.Data,entry+8)==size);
            Node name=Child(icons,new Key(U16(group.Data,entry+12)),false);
            Require(name!=null);
            Node frame=Child(name,new Key(0),false);
            Require(frame!=null&&frame.Children==null&&frame.Data.Length==size);
            for(int j=0;j<size;j++)Require(frame.Data[j]==icon[offset+j]);
        }
    }
    internal static void Validate(byte[] executable) { Parse(executable); }
    internal static JustEnoughTools.Actions.FixedAction ReadAction(byte[] executable)
    {
        Node entry=Child(Child(Child(Parse(executable).Root,new Key(10),false),new Key(JustEnoughTools.Actions.FixedActionResource.ResourceId),false),new Key(0),false);
        Require(entry!=null&&entry.Children==null);
        return JustEnoughTools.Actions.FixedActionResource.Decode(entry.Data);
    }
    internal static bool HaveSameIcon(byte[] first,byte[] second)
    {
        Image oldImage=Parse(first),newImage=Parse(second);
        foreach(int type in new int[]{3,14}){
            Node oldNode=Child(oldImage.Root,new Key(type),false),newNode=Child(newImage.Root,new Key(type),false);
            if(oldNode==null||newNode==null){if(oldNode!=newNode)return false;continue;}
            byte[] oldBytes=BuildTree(oldNode,0),newBytes=BuildTree(newNode,0);
            if(oldBytes.Length!=newBytes.Length)return false;
            for(int i=0;i<oldBytes.Length;i++)if(oldBytes[i]!=newBytes[i])return false;
        }
        return true;
    }
    internal static int PinnedIconIndex(byte[] executable)
    {
        Node groups=Child(Parse(executable).Root,new Key(14),false);
        if(groups!=null)foreach(Key key in groups.Children.Keys)if(key.Name==null&&key.Id>1&&key.Id<=65535)return -key.Id;
        return -1;
    }

}

}
