import { DelegateTest } from "Example";

if (module == require.main) {
    let actions = new DelegateTest();

    print("testcase: 无参数的委托");
    console.log("********** add");
    actions.onAction("add", function () {
        console.log("js action call");
    });

    console.log("********** call");
    actions.CallAction();

    actions.onAction("set", null);
    console.log("********** after clear, call again");
    actions.CallAction();

    print("testcase: 带参数的委托");
    actions.onActionWithArgs("set", (a, b, c) => {
        console.log(a, b, c);
    });
    actions.CallActionWithArgs("string", 123, 456);

    actions.onFunc("set", v => v * 2);
    console.log(actions.CallFunc(111));
    actions.onFunc("set", undefined);

    print("testcase: 事件");
    actions.onEvent("add", v => print("测试事件1:", v));
    function instanceEventHandler(v: number) { print("测试事件2:", v) }
    actions.onEvent("add", instanceEventHandler);
    actions.DipatchEvent(123);
    actions.onEvent("remove", instanceEventHandler);
    actions.DipatchEvent(123);

    print("testcase: 静态事件");
    DelegateTest.onStaticEvent("add", v => print("测试静态事件1:", v));
    function staticEventHandler(v: number) { print("测试静态事件2:", v) }
    DelegateTest.onStaticEvent("add", staticEventHandler);
    DelegateTest.DipatchStaticEvent(123);
    DelegateTest.onStaticEvent("remove", staticEventHandler);
    DelegateTest.DipatchStaticEvent(123);

    try {
        print("testcase: delegate with ref/out parameters");
        actions.complexCall("add", (b, a, v) => {
            a.value += b;
            v.value = 999;
            return 789;
        });
        actions.complexCall2("add", v => {
            v.value.Set(v.value.x * 2, v.value.y * 2, v.value.z * 2);
        });
        actions.TestComplexCall();
    } catch (err) {
        console.error(err);
    }
}
