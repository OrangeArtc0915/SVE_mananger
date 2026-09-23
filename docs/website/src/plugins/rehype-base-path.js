import { visit } from "unist-util-visit";

/**
 * 站点部署在子路径（GitHub Pages 项目页，base = /SVE_mananger/）时，
 * Markdown 正文里的根相对链接（/wiki/xxx/、/assets/xxx.png）会被浏览器
 * 送到域名根目录，必然 404。这里统一补上 base 前缀。
 * 已经带前缀的、锚点（#xxx）、协议相对（//host）与外链都不动。
 */
export function rehypeBasePath(options = {}) {
	const base = options.base || "/";
	const prefix = base.endsWith("/") ? base.slice(0, -1) : base;

	return (tree) => {
		if (!prefix) return;

		visit(tree, "element", (node) => {
			if (node.tagName !== "a" && node.tagName !== "img") return;

			const attr = node.tagName === "a" ? "href" : "src";
			const value = node.properties?.[attr];
			if (typeof value !== "string") return;
			if (!value.startsWith("/") || value.startsWith("//")) return;
			if (value === prefix || value.startsWith(`${prefix}/`)) return;

			node.properties[attr] = prefix + value;
		});
	};
}
