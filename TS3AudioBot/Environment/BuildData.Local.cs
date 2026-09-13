// Local "TSBot" build: version stamp (replaces the CI-generated Version.g.cs)
namespace TS3AudioBot.Environment;

public partial class BuildData
{
	partial void GetDataInternal()
	{
		Version = "0.13.0-alpha+tsbot";
		Branch = "develop";
		CommitSha = "cc2932880bd05f29a60304fb0165a5f12d73e1f3";
		BuildConfiguration = "Release";
	}
}
