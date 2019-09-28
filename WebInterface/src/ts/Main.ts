// Modules
import Vue from "vue";
import VueRouter from "vue-router";
import Buefy from "buefy";
// Css
import "@mdi/font/css/materialdesignicons.css";
import "buefy/dist/buefy.css";
import "../less/styles_formdata.less";
// Pages
import App from "./App.vue";
import Bot from "./Pages/Bot.vue";
import Bots from "./Pages/Bots.vue";
import BotServer from "./Pages/BotServer.vue";
import BotSettings from "./Pages/BotSettings.vue";
import Home from "./Pages/Home.vue";
import Overview from "./Pages/Overview.vue";
import Playlists from "./Pages/Playlists.vue";
import UiTests from "./Pages/UiTests.vue";

// export class Main {
// 	private static divAuthToken: HTMLInputElement;

// 	private static loadAuth() {
// 		const auth = window.localStorage.getItem("api_auth");
// 		if (auth) {
// 			Main.AuthData = ApiAuth.Create(auth);
// 			Main.divAuthToken.value = auth;
// 		}
// 	}

// 	private static authChanged() {
// 		Get.AuthData = ApiAuth.Create(Main.divAuthToken.value);
// 		window.localStorage.setItem("api_auth", Get.AuthData.getFullAuth());
// 		// todo do test auth
// 	}
// }

Vue.use(VueRouter);
Vue.use(Buefy);
Vue.directive("focus", {
	inserted(el) {
		let ichild = el as HTMLInputElement;
		if (ichild.tagName !== "input") {
			ichild = el.querySelector("input")!;
		}

		if (ichild) {
			ichild.focus();
			ichild.setSelectionRange(0, ichild.value.length);
			return;
		}

		el.focus();
	}
});

const router = new VueRouter({
	routes: [
		{ path: "/", component: Home },
		//{ path: "/openapi", component: Commands },
		{ path: "/overview", component: Overview },
		{ path: "/bots", component: Bots, name: "r_bots" },
		{ path: "/uitests", component: UiTests },
		{
			path: "/bot/:id",
			component: Bot,
			props: { online: true },
			children: [
				{
					name: "r_server",
					path: "server",
					component: BotServer
				},
				{
					name: "r_playlists",
					path: "playlists/:playlist",
					component: Playlists,
				},
				{
					name: "r_settings",
					path: "settings",
					component: BotSettings,
					props: { online: true }
				}
			]
		},
		{
			path: "/bot_offline/:name", component: Bot,
			props: { online: false },
			children: [
				{
					name: "r_settings_offline",
					path: "settings",
					component: BotSettings,
					props: { online: false }
				},
			]
		},
	]
});

export default new Vue({
	el: "#app",
	template: "<App/>",
	components: {
		App
	},
	router
});
