using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

var path = @"C:\Program Files (x86)\Steam\steamapps\common\A Dance of Fire and Ice\A Dance of Fire and Ice_Data\Managed\Assembly-CSharp.dll";
using var stream = File.OpenRead(path);
using var pe = new PEReader(stream);
var md = pe.GetMetadataReader();
var audio = new Dictionary<int, string>();
foreach (var handle in md.MemberReferences)
{
    var member = md.GetMemberReference(handle);
    if (member.Parent.Kind != HandleKind.TypeReference) continue;
    var type = md.GetTypeReference((TypeReferenceHandle)member.Parent);
    var name = md.GetString(type.Name);
    if (name is "AudioSource" or "AudioSettings" or "AudioListener" or "AudioClip" or "AudioMixer" or "AudioMixerGroup")
        audio[MetadataTokens.GetToken(handle)] = name + "." + md.GetString(member.Name);
}
var results = new SortedSet<string>();
foreach (var handle in md.TypeDefinitions)
{
    var type = md.GetTypeDefinition(handle);
    var typeName = md.GetString(type.Name);
    foreach (var methodHandle in type.GetMethods())
    {
        var method = md.GetMethodDefinition(methodHandle);
        if (method.RelativeVirtualAddress == 0) continue;
        var il = pe.GetMethodBody(method.RelativeVirtualAddress).GetILBytes();
        for (var i = 0; i + 4 <= il.Length; i++)
        {
            var token = BitConverter.ToInt32(il, i);
            if (audio.TryGetValue(token, out var member))
                results.Add(typeName + "." + md.GetString(method.Name) + " -> " + member);
        }
    }
}
foreach (var result in results) Console.WriteLine(result);

foreach (var handle in md.TypeDefinitions)
{
    var type = md.GetTypeDefinition(handle);
    var typeName = md.GetString(type.Name);
    if (typeName is not ("RDUtils" or "AudioManager" or "scrSfx" or "scrController")) continue;
    foreach (var methodHandle in type.GetMethods())
    {
        var method = md.GetMethodDefinition(methodHandle);
        var methodName = md.GetString(method.Name);
        if (!(methodName.Contains("Mixer") || methodName.Contains("Volume") || methodName.Contains("PlaySfx") || methodName == "Awake")) continue;
        if (method.RelativeVirtualAddress == 0) continue;
        var il = pe.GetMethodBody(method.RelativeVirtualAddress).GetILBytes();
        for (var i = 0; i + 5 <= il.Length; i++)
        {
            if (il[i] != 0x72) continue;
            var token = BitConverter.ToInt32(il, i + 1);
            if ((token & unchecked((int)0xff000000)) != 0x70000000) continue;
            try { Console.Error.WriteLine(typeName + "." + methodName + " string: " + md.GetUserString(MetadataTokens.UserStringHandle(token & 0xffffff))); } catch { }
        }
    }
}

using var audioStream = File.OpenRead(Path.Combine(Path.GetDirectoryName(path)!, "UnityEngine.AudioModule.dll"));
using var audioPe = new PEReader(audioStream);
var audioMd = audioPe.GetMetadataReader();
foreach (var handle in audioMd.TypeDefinitions)
{
    var type = audioMd.GetTypeDefinition(handle);
    var name = audioMd.GetString(type.Name);
    if (name is not ("AudioSource" or "AudioListener" or "AudioSettings" or "AudioClip")) continue;
    foreach (var methodHandle in type.GetMethods())
    {
        var method = audioMd.GetMethodDefinition(methodHandle);
        var methodName = audioMd.GetString(method.Name);
        if (methodName.Contains("Play") || methodName.Contains("Stop") || methodName.Contains("Pause") || methodName.Contains("Scheduled") || methodName.Contains("Spectrum") || methodName.Contains("GetData") || methodName.StartsWith("get_") || methodName.StartsWith("set_"))
            Console.Error.WriteLine(name + "." + methodName + " params=" + method.GetParameters().Count);
    }
}
