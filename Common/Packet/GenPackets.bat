START ../../PacketGenerator/bin/Debug/PacketGenerator.exe ../../PacketGenerator/PDL.xml
XCOPY /y GenPackets.cs "../../DummyClient/Packet"
XCOPY /y GenPackets.cs "../../Server/Packet"
XCOPY /y ClientPacketManager.cs "../../DummyClient/Packet"
XCOPY /y ServerPacketManager.cs "../../Server/Packet"