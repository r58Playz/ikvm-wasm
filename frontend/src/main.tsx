import { css, FC } from "dreamland/core";
import "./style.css";
import { dotnetState, initDotnet, play } from "./dotnet";

function App(this: FC<{}, {}>) {
	this.cx.mount = async () => {
		await initDotnet();
		await play();
	};

	return (
		<div>
			{use(dotnetState.logs).mapEach(x => <div>{x}</div>)}
		</div>
	)
}
App.style = css`
	:scope {
		overflow: scroll;
		height: 100%;
		font-family: ui-monospace, SFMono-Regular, "SF Mono", Consolas, "Liberation Mono", Menlo, monospace;
		white-space: pre-wrap;
	}
`;

document.querySelector("#app")!.replaceWith(<App />);
