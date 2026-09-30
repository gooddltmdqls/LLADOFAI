msbuild LLADOFAI.sln /p:Configuration=Release /p:Platform="Any CPU" -m
set /p version=<VERSION.txt
mkdir tmp
cd tmp
mkdir LLADOFAI
copy ..\images\asio_logo.png LLADOFAI
copy ..\Info.json LLADOFAI
copy ..\LLADOFAI\bin\Release\LLADOFAI.dll LLADOFAI
copy ..\LLADOFAI.NAudio\bin\Release\LLADOFAI.NAudio.dll LLADOFAI
copy ..\LLADOFAI.Fmod\bin\Release\LLADOFAI.Fmod.dll LLADOFAI
rem The FMOD Core runtime (fmod.dll) is licensed by Firelight Technologies.
rem This mod uses FMOD in non-commercial mode, so redistribution is permitted.
copy ..\packages\fmod.dll LLADOFAI
copy ..\packages\Microsoft.Win32.Registry.4.7.0\lib\net461\Microsoft.Win32.Registry.dll LLADOFAI
copy ..\LLADOFAI\bin\Release\NAudio.Core.dll LLADOFAI
copy ..\LLADOFAI\bin\Release\NAudio.Asio.dll LLADOFAI
copy ..\LLADOFAI\bin\Release\NAudio.Wasapi.dll LLADOFAI
copy ..\packages\System.Security.AccessControl.4.7.0\lib\net461\System.Security.AccessControl.dll LLADOFAI
copy ..\packages\System.Security.Principal.Windows.4.7.0\lib\net461\System.Security.Principal.Windows.dll LLADOFAI
tar -a -c -f LLADOFAI-%version%.zip LLADOFAI
move LLADOFAI-%version%.zip ..
cd ..
rmdir /s /q tmp
pause
