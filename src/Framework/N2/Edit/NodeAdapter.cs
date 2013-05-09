using System;
using System.Linq;
using System.Collections.Generic;
using N2.Collections;
using N2.Edit.FileSystem;
using N2.Edit.Workflow;
using N2.Engine;
using N2.Security;
using N2.Web;
using N2.Edit.Settings;
using N2.Definitions;
using N2.Persistence.Sources;
using System.Text;
using N2.Engine.Globalization;
using N2.Edit.Versioning;

namespace N2.Edit
{
	[Adapts(typeof(ContentItem))]
	public class NodeAdapter : AbstractContentAdapter
	{
		private IEditUrlManager editUrlManager;
		private IWebContext webContext;
		private IHost host;
		private IFileSystem fileSystem;
		private VirtualNodeFactory nodeFactory;
		private ISecurityManager security;
		private NavigationSettings settings;
		private ContentSource sources;
		private IDefinitionManager definitions;
		private ILanguageGateway languages;
		private DraftRepository drafts;

		public NavigationSettings Settings
		{
			get { return settings ?? Engine.Resolve<NavigationSettings>(); }
			set { settings = value; }
		}

		public ISecurityManager Security
		{
			get { return security ?? Engine.Resolve<ISecurityManager>(); }
			set { security = value; }
		}

		public IWebContext WebContext
		{
			get { return webContext ?? Engine.Resolve<IWebContext>(); }
			set { webContext = value; }
		}

		public VirtualNodeFactory NodeFactory
		{
			get { return nodeFactory ?? Engine.Resolve<VirtualNodeFactory>(); }
			set { nodeFactory = value; }
		}

		public IFileSystem FileSystem
		{
			get { return fileSystem ?? Engine.Resolve<IFileSystem>(); }
			set { fileSystem = value; }
		}

		public IHost Host
		{
			get { return host ?? Engine.Resolve<IHost>(); }
			set { host = value; }
		}

		public IEditUrlManager ManagementPaths
		{
			get { return editUrlManager ?? Engine.ManagementPaths; }
			set { editUrlManager = value; }
		}

		public ContentSource Sources
		{
			get { return sources ?? Engine.Resolve<ContentSource>(); }
			set { sources = value; }
		}

		public IDefinitionManager Definitions
		{
			get { return definitions ?? Engine.Resolve<IDefinitionManager>(); }
			set { definitions = value; }
		}

		public ILanguageGateway Languages
		{
			get { return languages ?? Engine.Resolve<ILanguageGateway>(); }
			set { languages = value; }
		}

		public DraftRepository Drafts
		{
			get { return drafts ?? Engine.Resolve<DraftRepository>(); }
			set { drafts = value; }
		}


		/// <summary>Gets the node representation used to build the tree hierarchy in the management UI.</summary>
		/// <param name="item">The item to link to.</param>
		/// <returns>Tree node data.</returns>
		public virtual TreeNode GetTreeNode(ContentItem item)
		{
			var node = new TreeNode
			{
				ID = item.ID,
				Path = item.Path,
				State = item.State,
				IconUrl = GetIconUrl(item),
				Title = item.Title,
				ToolTip = "#" + item.ID + ": " +  Definitions.GetDefinition(item).Title,
				PreviewUrl = GetPreviewUrl(item),
				MaximumPermission = GetMaximumPermission(item),
				SortOrder = item.SortOrder
			};
			
			node.MetaInformation = GetMetaInformation(item);
			ApplyStateFlags(node, item);
			return node;
		}

		protected virtual IDictionary<string, MetaInfo> GetMetaInformation(ContentItem item)
		{
			var mi = new Dictionary<string, MetaInfo>();

			if (Languages.IsLanguageRoot(item) && Languages.GetLanguage(item) != null)
				mi["language"] = new MetaInfo { Text = Languages.GetLanguage(item).LanguageCode };
			
			if(!item.IsPage)
				mi["zone"] = new MetaInfo { Text = item.ZoneName };
			
			if (Host.IsStartPage(item))
				mi["authority"] = new MetaInfo { Text = string.IsNullOrEmpty(Host.GetSite(item).Authority) ? "*" : Host.GetSite(item).Authority };
			
			var draftInfo = Drafts.GetDraftInfo(item);
			if (draftInfo != null && draftInfo.Saved > item.Updated)
				mi["draft"] = new MetaInfo { Text = "&nbsp;", ToolTip = draftInfo.SavedBy + ": " + draftInfo.Saved };

			return mi;
		}

		private void ApplyStateFlags(TreeNode node, ContentItem item)
		{
			StringBuilder className = new StringBuilder();

			if (!item.IsPublished())
			{
				className.Append("unpublished ");
			}
			else if (item.Published > DateTime.Now.AddDays(-1))
			{
				className.Append("day ");
			}
			else if (item.Published > DateTime.Now.AddDays(-7))
			{
				className.Append("week ");
			}
			else if (item.Published > DateTime.Now.AddMonths(-1))
			{
				className.Append("month ");
			}

			if (item.IsExpired())
			{
				className.Append("expired ");
			}

			if (!item.Visible)
			{
				className.Append("invisible ");
			}

			if (item.AlteredPermissions != Permission.None && item.AuthorizedRoles != null && item.AuthorizedRoles.Count > 0)
			{
				className.Append("locked ");
			}

			node.CssClass = className.ToString();
		}

		public virtual IEnumerable<DirectoryData> GetUploadDirectories(Site site)
		{
			foreach (var uploadFolder in site.UploadFolders.Where(uf => uf.Readers.Authorizes(WebContext.User, null, Permission.Read)))
			{
				yield return FileSystem.GetDirectoryOrVirtual(uploadFolder.Path);
			}
		}

		/// <summary>Gets the children of the given item for the given user interface.</summary>
		/// <param name="parent">The item whose children to get.</param>
		/// <param name="userInterface">The interface where the children are displayed.</param>
		/// <returns>An enumeration of the children.</returns>
		public virtual IEnumerable<ContentItem> GetChildren(ContentItem parent, string userInterface)
		{
			return GetChildren(new Query { Parent = parent, Interface = userInterface });
		}

		/// <summary>Gets the children of the given item for the given user interface.</summary>
		/// <param name="parent">The item whose children to get.</param>
		/// <param name="userInterface">The interface where the children are displayed.</param>
		/// <returns>An enumeration of the children.</returns>
		public virtual IEnumerable<ContentItem> GetChildren(Query query)
		{
			IEnumerable<ContentItem> children = GetNodeChildren(query);

			foreach (var child in children)
				yield return child;

			if (Interfaces.Managing == query.Interface)
			{
				foreach (var child in NodeFactory.GetChildren(query.Parent.Path))
				{
					yield return child;
				}
			}
		}

		protected virtual IEnumerable<ContentItem> GetNodeChildren(ContentItem parent)
		{
			return GetNodeChildren(new Query { Parent = parent, Interface = Interfaces.Viewing });
		}

		protected virtual IEnumerable<ContentItem> GetNodeChildren(Query query)
		{
			if (query == null) throw new ArgumentNullException("query");

			if (query.Parent is IActiveChildren)
				return ((IActiveChildren)query.Parent).GetChildren(new AccessFilter(WebContext.User, Security));

			if (!Settings.DisplayDataItems)
				query.OnlyPages = true;

			var children = Sources.GetChildren(query);

			return children;
		}

		/// <summary>Returns true when an item has children.</summary>
		/// <param name="filter">The filter to apply.</param>
		/// <param name="parent">The item whose childrens existence is to be determined.</param>
		/// <returns>True when there are children.</returns>
		public virtual bool HasChildren(ContentItem parent, ItemFilter filter)
		{
			return Sources.HasChildren(new Query { Parent = parent, Filter = filter, Interface = Interfaces.Managing });
		}

		/// <summary>Gets the url used from the management UI when previewing an item.</summary>
		/// <param name="item">The item to preview.</param>
		/// <returns>An url to preview the item.</returns>
		public virtual string GetPreviewUrl(ContentItem item)
		{
			string url = ManagementPaths.GetPreviewUrl(item);
			url = String.IsNullOrEmpty(url) ? ManagementPaths.ResolveResourceUrl("{ManagementUrl}/Empty.aspx") : url;
			var viewPrefrence = WebContext.HttpContext.GetViewPreference(Engine.Config.Sections.Management.Versions.DefaultViewMode);
			url = url.ToUrl().AppendViewPreference(viewPrefrence, ViewPreference.Published);
			return url;
		}

		/// <summary>Gets the url used from the management UI when displaying the navigation tree.</summary>
		/// <param name="item">The item to preview.</param>
		/// <returns>An url to preview the item.</returns>
		public virtual string GetNavigationUrl(ContentItem item)
		{
			return ManagementPaths.GetNavigationUrl(item)
				.ToUrl().AppendViewPreference(WebContext.HttpContext.GetViewPreference(Engine.Config.Sections.Management.Versions.DefaultViewMode), Engine.Config.Sections.Management.Versions.DefaultViewMode);
		}

		/// <summary>Gets the url to the icon representing this item.</summary>
		/// <param name="item">The item whose icon to get.</param>
		/// <returns>An url to an icon.</returns>
		public virtual string GetIconUrl(ContentItem item)
		{
			return Url.ResolveTokens(item.IconUrl);
		}

		/// <summary>Gets the permissions for the logged in user towards an item.</summary>
		/// <param name="item">The item for which permissions should be retrieved.</param>
		/// <returns>A permission flag.</returns>
		public virtual Permission GetMaximumPermission(ContentItem item)
		{
			return PermissionMap.GetMaximumPermission(Security.GetPermissions(WebContext.User, item));
		}
	}
}
