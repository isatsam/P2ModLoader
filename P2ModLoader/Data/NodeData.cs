using System.Xml.Linq;

namespace P2ModLoader.Data;

public class NodeData {
	public enum NodeType {
		Unknown,
		Profile,
		Save
	}
	
	public NodeType Type { get; init; }
	public XElement? XElement { get; init; } 
	public string? Path { get; init; } 
}